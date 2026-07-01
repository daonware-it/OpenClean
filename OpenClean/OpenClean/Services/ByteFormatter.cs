namespace OpenClean.Services;

/// <summary>
/// Wandelt Byte-Größen in menschenlesbare Zeichenketten (KB/MB/GB) um.
/// Von Bereinigung und Autostart gemeinsam genutzt.
/// </summary>
public static class ByteFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {Units[unit]}"
            : $"{value:0.##} {Units[unit]}";
    }
}
