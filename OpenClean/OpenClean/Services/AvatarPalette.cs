using System.Windows.Media;

namespace OpenClean.Services;

/// <summary>
/// Deterministischer Buchstaben-Avatar als Fallback, wenn für ein Programm kein
/// echtes Icon extrahiert werden kann: Anfangsbuchstabe + stabile Farbe aus dem
/// Namen. Wird von der Deinstallations- und der Update-Liste gemeinsam genutzt.
/// </summary>
public static class AvatarPalette
{
    private static readonly Brush[] Brushes = CreateBrushes();

    /// <summary>Erster Buchstabe/Ziffer des Namens (Großbuchstabe), sonst „•“.</summary>
    public static string InitialFor(string name)
    {
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return "•";
    }

    /// <summary>Deterministische Avatar-Farbe aus dem Namen (stabil pro Programm).</summary>
    public static Brush BrushFor(string name)
    {
        int hash = 0;
        foreach (char c in name) hash = hash * 31 + c;
        return Brushes[(hash & int.MaxValue) % Brushes.Length];
    }

    private static Brush[] CreateBrushes()
    {
        string[] colors = { "#4F7CFF", "#34D399", "#F59E0B", "#A78BFA", "#F472B6",
                            "#22D3EE", "#FB7185", "#84CC16", "#38BDF8", "#FBBF24" };
        var list = new Brush[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            b.Freeze();
            list[i] = b;
        }
        return list;
    }
}
