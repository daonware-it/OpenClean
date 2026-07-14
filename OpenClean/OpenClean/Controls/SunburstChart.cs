using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenClean.Models;
using OpenClean.Services;

namespace OpenClean.Controls;

/// <summary>
/// Sunburst-Diagramm der Datenträgerbelegung: verschachtelte Ringe, in denen der
/// Öffnungswinkel eines Segments proportional zur Größe des Ordners innerhalb seines
/// Elternteils ist. Ein Klick auf ein Segment zoomt in den Ordner hinein, ein Klick in die
/// Mitte eine Ebene zurück.
///
/// <para>Wie <see cref="ScoreRing"/> ein reines <see cref="FrameworkElement"/> mit
/// <see cref="OnRender"/> – die App bringt bewusst keine Chart-Bibliothek mit.</para>
///
/// <para>Für das Hit-Testing merkt sich das Control beim Zeichnen zu jedem Segment die
/// erzeugte <see cref="Geometry"/>. Segmente unter <see cref="MinAngle"/> Grad werden pro
/// Ring zu einem grauen „Sonstiges"-Segment zusammengefasst: Haarlinien wären weder
/// sichtbar noch klickbar.</para>
/// </summary>
public sealed class SunburstChart : FrameworkElement
{
    /// <summary>Wie viele Ringe (Ebenen) gezeichnet werden.</summary>
    public const int RingCount = 3;

    /// <summary>Segmente darunter werden zu „Sonstiges" zusammengefasst (Grad).</summary>
    private const double MinAngle = 1.5;

    /// <summary>Radius des freien Lochs in der Mitte, relativ zum Gesamtradius.</summary>
    private const double HoleRatio = 0.30;

    /// <summary>Beim Zeichnen aufgebaute Trefferliste (Geometrie → Ordner).</summary>
    private readonly List<(Geometry Geometry, FolderNode Node)> _hitAreas = new();

    private FolderNode? _hovered;

    // ---- Dependency Properties ---------------------------------------------

    public static readonly DependencyProperty RootProperty =
        DependencyProperty.Register(nameof(Root), typeof(FolderNode), typeof(SunburstChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Der aktuell angezeigte Ordner (Mitte des Diagramms).</summary>
    public FolderNode? Root
    {
        get => (FolderNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public static readonly DependencyProperty DrillDownCommandProperty =
        DependencyProperty.Register(nameof(DrillDownCommand), typeof(ICommand), typeof(SunburstChart),
            new PropertyMetadata(null));

    /// <summary>Wird mit dem angeklickten <see cref="FolderNode"/> aufgerufen.</summary>
    public ICommand? DrillDownCommand
    {
        get => (ICommand?)GetValue(DrillDownCommandProperty);
        set => SetValue(DrillDownCommandProperty, value);
    }

    public static readonly DependencyProperty NavigateUpCommandProperty =
        DependencyProperty.Register(nameof(NavigateUpCommand), typeof(ICommand), typeof(SunburstChart),
            new PropertyMetadata(null));

    /// <summary>Wird bei Klick auf die Mitte aufgerufen (ohne Parameter).</summary>
    public ICommand? NavigateUpCommand
    {
        get => (ICommand?)GetValue(NavigateUpCommandProperty);
        set => SetValue(NavigateUpCommandProperty, value);
    }

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(SunburstChart),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Farbe für Bytes, die direkt im Ordner liegen (also zu keinem Unterordner gehören).
    /// <see cref="DiskScannerService"/> zählt solche Dateien in <see cref="FolderNode.TotalBytes"/>
    /// mit, ohne dafür einen <see cref="FolderNode"/> in <see cref="FolderNode.Children"/> anzulegen –
    /// ohne dieses Segment bliebe an dieser Stelle eine unbeschriftete, nicht anklickbare Lücke im Ring.
    /// </summary>
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public static readonly DependencyProperty OtherBrushProperty =
        DependencyProperty.Register(nameof(OtherBrush), typeof(Brush), typeof(SunburstChart),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Farbe der „Sonstiges"-Sammelsegmente.</summary>
    public Brush OtherBrush
    {
        get => (Brush)GetValue(OtherBrushProperty);
        set => SetValue(OtherBrushProperty, value);
    }

    public static readonly DependencyProperty StrokeBrushProperty =
        DependencyProperty.Register(nameof(StrokeBrush), typeof(Brush), typeof(SunburstChart),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Trennlinie zwischen den Segmenten (Hintergrundfarbe der Karte).</summary>
    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public static readonly DependencyProperty CenterTextBrushProperty =
        DependencyProperty.Register(nameof(CenterTextBrush), typeof(Brush), typeof(SunburstChart),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Textfarbe der Beschriftung in der Mitte.</summary>
    public Brush CenterTextBrush
    {
        get => (Brush)GetValue(CenterTextBrushProperty);
        set => SetValue(CenterTextBrushProperty, value);
    }

    // ---- Farbpalette --------------------------------------------------------

    /// <summary>
    /// Basis-Farbtöne der Top-Level-Segmente (HSL-Farbton in Grad). Kindsegmente erben den
    /// Ton ihres Vorfahren und werden nach außen hin heller – so bleibt optisch erkennbar,
    /// welcher Ast wohin gehört.
    /// </summary>
    private static readonly double[] BaseHues = { 158, 205, 265, 25, 340, 45, 185, 300 };

    protected override void OnRender(DrawingContext dc)
    {
        _hitAreas.Clear();

        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        var root = Root;
        if (root is null || root.TotalBytes <= 0) return;

        var center = new Point(w / 2, h / 2);
        double outerRadius = Math.Min(w, h) / 2 - 2;
        double holeRadius = outerRadius * HoleRatio;
        double ringWidth = (outerRadius - holeRadius) / RingCount;

        // Ebene 1 beginnt am Loch; jede weitere Ebene liegt einen Ring weiter außen.
        DrawLevel(dc, root, center, holeRadius, ringWidth, startAngle: 0, sweep: 360,
            depth: 0, hue: -1);

        // Freie Mitte: Fläche für den Zurück-Klick + Beschriftung.
        dc.DrawEllipse(StrokeBrush, null, center, holeRadius, holeRadius);
        DrawCenterLabel(dc, root, center, holeRadius);
    }

    private void DrawLevel(
        DrawingContext dc, FolderNode node, Point center,
        double innerRadius, double ringWidth, double startAngle, double sweep, int depth, double hue)
    {
        if (depth >= RingCount) return;
        if (node.TotalBytes <= 0) return;

        // Kinder absteigend: Die großen Segmente landen zusammen am Ringanfang.
        // Bewusst nicht mehr durch "Children.Count == 0" kurzgeschlossen (Abweichung vom
        // Brief-Code): Ein kinderloser Ordner besteht ausschließlich aus eigenen Dateien und
        // muss den Ring trotzdem füllen (siehe TrackBrush unten) – sonst bliebe dort eine
        // unbeschriftete Lücke.
        var children = node.Children.OrderByDescending(c => c.TotalBytes).ToList();

        double angle = startAngle;
        double otherBytes = 0;
        double childrenBytes = 0;

        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            childrenBytes += child.TotalBytes;
            double childSweep = sweep * child.TotalBytes / node.TotalBytes;

            // Zu dünn zum Zeichnen und zum Klicken: sammeln und am Ende zusammen zeigen.
            if (childSweep < MinAngle)
            {
                otherBytes += child.TotalBytes;
                continue;
            }

            // Top-Level: eigener Farbton. Tiefer: Farbton des Vorfahren, heller.
            // Index aus der Schleife statt "children.IndexOf(child)" (Abweichung vom
            // Brief-Code): IndexOf durchsucht bei jedem Kind erneut dieselbe Liste, macht die
            // Schleife O(n²) – bei Ordnern mit vielen Unterordnern unnötig teuer.
            double childHue = depth == 0
                ? BaseHues[i % BaseHues.Length]
                : hue;
            double lightness = 0.42 + depth * 0.12;
            Brush fill = new SolidColorBrush(FromHsl(childHue, 0.55, lightness));
            fill.Freeze();

            var geometry = BuildSegment(center, innerRadius, innerRadius + ringWidth, angle, childSweep);
            dc.DrawGeometry(fill, new Pen(StrokeBrush, 1), geometry);
            _hitAreas.Add((geometry, child));

            // Kinder dieses Segments in den nächsten Ring.
            DrawLevel(dc, child, center, innerRadius + ringWidth, ringWidth,
                angle, childSweep, depth + 1, childHue);

            angle += childSweep;
        }

        // Sammelsegment „Sonstiges" – nicht klickbar (kein einzelner Ordner dahinter).
        if (otherBytes > 0)
        {
            double otherSweep = sweep * otherBytes / node.TotalBytes;
            if (otherSweep > 0.2)
            {
                var geometry = BuildSegment(center, innerRadius, innerRadius + ringWidth, angle, otherSweep);
                dc.DrawGeometry(OtherBrush, new Pen(StrokeBrush, 1), geometry);
            }
            angle += otherSweep;
        }

        // Eigene Dateien dieses Ordners (liegen in keinem Unterordner, siehe TrackBrush-Doku
        // oben) füllen den verbleibenden Rest des Rings. Abweichung vom Brief-Code: Ohne dieses
        // Segment bliebe hier eine leere, nicht anklickbare Lücke, und die im Interface
        // geforderte TrackBrush-Property (Step "Produces") bliebe ungenutzt.
        double ownBytes = node.TotalBytes - childrenBytes;
        if (ownBytes > 0)
        {
            double ownSweep = sweep * ownBytes / node.TotalBytes;
            if (ownSweep > 0.2)
            {
                var geometry = BuildSegment(center, innerRadius, innerRadius + ringWidth, angle, ownSweep);
                dc.DrawGeometry(TrackBrush, new Pen(StrokeBrush, 1), geometry);
            }
        }
    }

    /// <summary>Ringsegment als geschlossene Geometrie (zwei Bögen, zwei Radiallinien).</summary>
    private static Geometry BuildSegment(
        Point center, double innerRadius, double outerRadius, double startAngle, double sweep)
    {
        // 12 Uhr = 0°, im Uhrzeigersinn.
        Point Polar(double radius, double degrees)
        {
            double rad = (degrees - 90) * Math.PI / 180.0;
            return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
        }

        // Volle 360°: Start- und Endpunkt des Bogens fielen sonst zusammen (Start == Ende) und
        // WPFs ArcTo zeichnet dann gar nichts – analog zum Vollkreis-Sonderfall in ScoreRing,
        // hier statt eines Spezialfalls per Kappung gelöst, da BuildSegment eine geschlossene
        // Ring-Geometrie liefert (nicht nur einen Pfad wie ScoreRing) und daher schon mit einer
        // hauchdünnen Lücke unter der Sichtbarkeitsgrenze visuell nicht auffällt.
        sweep = Math.Min(sweep, 359.99);
        double endAngle = startAngle + sweep;
        bool isLarge = sweep > 180;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(innerRadius, startAngle), isFilled: true, isClosed: true);
            ctx.LineTo(Polar(outerRadius, startAngle), true, false);
            ctx.ArcTo(Polar(outerRadius, endAngle), new Size(outerRadius, outerRadius), 0,
                isLarge, SweepDirection.Clockwise, true, false);
            ctx.LineTo(Polar(innerRadius, endAngle), true, false);
            ctx.ArcTo(Polar(innerRadius, startAngle), new Size(innerRadius, innerRadius), 0,
                isLarge, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawCenterLabel(DrawingContext dc, FolderNode root, Point center, double radius)
    {
        var typeface = new Typeface("Segoe UI");

        var name = new FormattedText(root.Name, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, 13, CenterTextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var size = new FormattedText(ByteFormatter.Format(root.TotalBytes), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, 16, CenterTextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        name.MaxTextWidth = radius * 1.7;
        name.Trimming = TextTrimming.CharacterEllipsis;
        name.MaxLineCount = 1;

        dc.DrawText(name, new Point(center.X - name.Width / 2, center.Y - name.Height - 2));
        dc.DrawText(size, new Point(center.X - size.Width / 2, center.Y + 2));
    }

    // ---- Interaktion --------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var node = HitTest(e.GetPosition(this));
        if (ReferenceEquals(node, _hovered)) return;

        _hovered = node;
        Cursor = node is not null ? Cursors.Hand : Cursors.Arrow;
        ToolTip = node is null
            ? null
            : $"{node.FullPath}\n{ByteFormatter.Format(node.TotalBytes)}";
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        ToolTip = null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        var position = e.GetPosition(this);
        var node = HitTest(position);

        if (node is not null)
        {
            if (DrillDownCommand?.CanExecute(node) == true) DrillDownCommand.Execute(node);
            return;
        }

        // Kein Segment getroffen: War es die Mitte? Dann eine Ebene zurück.
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double holeRadius = (Math.Min(ActualWidth, ActualHeight) / 2 - 2) * HoleRatio;
        double dx = position.X - center.X, dy = position.Y - center.Y;

        if (dx * dx + dy * dy <= holeRadius * holeRadius
            && NavigateUpCommand?.CanExecute(null) == true)
        {
            NavigateUpCommand.Execute(null);
        }
    }

    /// <summary>Innerstes zuerst geprüft wäre falsch – die zuletzt gezeichneten (äußeren)
    /// Segmente liegen oben, deshalb rückwärts durch die Trefferliste.</summary>
    private FolderNode? HitTest(Point point)
    {
        for (int i = _hitAreas.Count - 1; i >= 0; i--)
        {
            if (_hitAreas[i].Geometry.FillContains(point))
                return _hitAreas[i].Node;
        }
        return null;
    }

    /// <summary>HSL → RGB. Erlaubt es, einen Farbton pro Ast zu halten und nur die
    /// Helligkeit mit der Tiefe zu variieren.</summary>
    private static Color FromHsl(double hueDegrees, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double hp = (hueDegrees % 360) / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        double m = lightness - c / 2;

        (double r, double g, double b) = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _   => (c, 0.0, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
