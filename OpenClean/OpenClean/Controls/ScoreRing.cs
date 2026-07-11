using System;
using System.Windows;
using System.Windows.Media;

namespace OpenClean.Controls;

/// <summary>
/// Ringförmige Score-Anzeige (0–100): ein voller Hintergrund-Ring plus ein farbiger
/// Fortschrittsbogen, der oben beginnt und im Uhrzeigersinn wächst. Die Zahl selbst
/// wird in XAML mittig über den Ring gelegt. Farbe über <see cref="RingBrush"/>
/// (per Style an die Score-Stufe gebunden), damit Dark/Light automatisch passen.
/// </summary>
public sealed class ScoreRing : FrameworkElement
{
    public static readonly DependencyProperty ScoreProperty =
        DependencyProperty.Register(nameof(Score), typeof(double), typeof(ScoreRing),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Score von 0 bis 100.</summary>
    public double Score
    {
        get => (double)GetValue(ScoreProperty);
        set => SetValue(ScoreProperty, value);
    }

    public static readonly DependencyProperty RingBrushProperty =
        DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(ScoreRing),
            new FrameworkPropertyMetadata(Brushes.MediumSeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Farbe des Fortschrittsbogens.</summary>
    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(ScoreRing),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Farbe des Hintergrund-Rings.</summary>
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(ScoreRing),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Ringstärke in Pixeln.</summary>
    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        double t = Thickness;
        double r = (Math.Min(w, h) - t) / 2;
        if (r <= 0) return;
        var c = new Point(w / 2, h / 2);

        // Hintergrund-Ring (voller Kreis).
        dc.DrawEllipse(null, new Pen(TrackBrush, t), c, r, r);

        double frac = Math.Clamp(Score / 100.0, 0, 1);
        if (frac <= 0) return;

        var pen = new Pen(RingBrush, t)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        // Voller Kreis: ArcSegment kann 360° (Start == Ende) nicht zeichnen.
        if (frac >= 0.999)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }

        var start = new Point(c.X, c.Y - r); // oben (12 Uhr)
        double angle = frac * 360.0;
        double rad = (angle - 90) * Math.PI / 180.0;
        var end = new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
