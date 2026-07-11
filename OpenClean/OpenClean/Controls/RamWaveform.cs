using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace OpenClean.Controls;

/// <summary>
/// Eine live mitlaufende, gefüllte Wellenform der RAM-Auslastung (0–100 %).
/// Ersetzt den früheren statischen Balken bzw. die Equalizer-Optik: ein rollender
/// Sample-Puffer bildet die Auslastung als weiche Kurve ab, deren Fläche bis zum
/// unteren Rand gefüllt ist (wie eine Audio-Wellenform).
///
/// Reine Anzeige – kein „RAM-Booster", keine Aktion. Die Farbe kommt aus dem Theme
/// (<c>AccentBrush</c>, bei kritischer Auslastung <c>DangerBrush</c>), sodass Dark/Light
/// automatisch passen.
/// </summary>
public sealed class RamWaveform : FrameworkElement
{
    /// <summary>Aktuelle RAM-Auslastung in Prozent (0–100). Von außen gebunden.</summary>
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(double), typeof(RamWaveform),
            new PropertyMetadata(0.0, OnLevelChanged));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    // Anzahl der historischen Stützstellen (Breite des sichtbaren Zeitfensters).
    private const int SampleCount = 120;
    // Schwelle, ab der die Kurve in die Warnfarbe wechselt.
    private const double CriticalLevel = 90;

    private readonly double[] _samples = new double[SampleCount];
    private int _writeIndex;
    private bool _seeded;

    private double _current;      // geglätteter Ist-Wert (folgt Level sanft)
    private double _target;       // Zielwert aus Level
    private double _phase;        // Phase der Wellen-Textur
    private TimeSpan _lastTick;

    public RamWaveform()
    {
        // Kontinuierlich rendern, solange das Control sichtbar ist; sauber ab-/anmelden.
        Loaded += (_, _) => { _lastTick = TimeSpan.Zero; CompositionTarget.Rendering += OnRendering; };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var w = (RamWaveform)d;
        w._target = Math.Clamp((double)e.NewValue, 0, 100);
        if (!w._seeded)
        {
            // Puffer beim ersten echten Wert flach vorbelegen (kein „Hochlaufen" aus 0).
            w._current = w._target;
            for (int i = 0; i < SampleCount; i++) w._samples[i] = w._target;
            w._seeded = true;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args) return;
        if (_lastTick == TimeSpan.Zero) { _lastTick = args.RenderingTime; return; }

        double dt = (args.RenderingTime - _lastTick).TotalSeconds;
        if (dt < 0.02) return; // ~50 Hz drosseln
        _lastTick = args.RenderingTime;

        // Ist-Wert sanft zum Ziel führen (kein hartes Springen bei Messwertwechsel).
        _current += (_target - _current) * Math.Min(1, dt * 3);

        // Lebendige, deterministische Wellen-Textur um den Ist-Wert.
        _phase += dt * 2.2;
        double wobble = Math.Sin(_phase) * 2.2 + Math.Sin(_phase * 2.7 + 1.0) * 1.4;
        double sample = Math.Clamp(_current + wobble, 0, 100);

        _samples[_writeIndex] = sample;
        _writeIndex = (_writeIndex + 1) % SampleCount;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        // Chronologisch geordnete Punkte (ältestes am Schreibindex) auf die Fläche abbilden.
        var pts = new List<Point>(SampleCount);
        for (int i = 0; i < SampleCount; i++)
        {
            double v = _seeded ? _samples[(_writeIndex + i) % SampleCount] : 0;
            double x = SampleCount == 1 ? w : i / (double)(SampleCount - 1) * w;
            double y = h - Math.Clamp(v, 0, 100) / 100.0 * h;
            pts.Add(new Point(x, y));
        }

        Color accent = ResolveColor("AccentBrush", Color.FromRgb(0x34, 0xD3, 0x99));
        bool critical = _current >= CriticalLevel;
        if (critical) accent = ResolveColor("DangerBrush", Color.FromRgb(0xF8, 0x71, 0x71));

        // Gefüllte Fläche: Kurve → bis zur Unterkante → geschlossen.
        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            ctx.LineTo(pts[0], false, false);
            AppendSmooth(ctx, pts);
            ctx.LineTo(new Point(w, h), false, false);
        }
        fill.Freeze();

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x73, accent.R, accent.G, accent.B), 0.0),
                new GradientStop(Color.FromArgb(0x22, accent.R, accent.G, accent.B), 0.55),
                new GradientStop(Color.FromArgb(0x05, accent.R, accent.G, accent.B), 1.0),
            }
        };
        gradient.Freeze();
        dc.DrawGeometry(gradient, null, fill);

        // Obere Konturlinie.
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            AppendSmooth(ctx, pts);
        }
        line.Freeze();

        var stroke = new SolidColorBrush(accent);
        stroke.Freeze();
        dc.DrawGeometry(null, new Pen(stroke, 2.0)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, line);
    }

    /// <summary>Fügt die Punkte als weiche Kurve (Mittelpunkt-Quadratik) an eine Figur an.</summary>
    private static void AppendSmooth(StreamGeometryContext ctx, IReadOnlyList<Point> pts)
    {
        for (int i = 1; i < pts.Count; i++)
        {
            var prev = pts[i - 1];
            var cur = pts[i];
            var mid = new Point((prev.X + cur.X) / 2, (prev.Y + cur.Y) / 2);
            ctx.QuadraticBezierTo(prev, mid, true, false);
        }
        ctx.LineTo(pts[^1], true, false);
    }

    /// <summary>Liest eine Theme-Farbe (SolidColorBrush) aus den Ressourcen, sonst Fallback.</summary>
    private Color ResolveColor(string resourceKey, Color fallback)
        => TryFindResource(resourceKey) is SolidColorBrush b ? b.Color : fallback;
}
