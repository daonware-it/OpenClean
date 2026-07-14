using System.IO;

namespace OpenClean.Models;

/// <summary>Eine vom Große-Dateien-Finder gefundene Datei.</summary>
public sealed record LargeFile(string FullPath, long SizeBytes, DateTime LastWriteTime)
{
    /// <summary>Dateiendung ohne Punkt und in Großbuchstaben, z. B. „ISO"; leer, wenn keine.</summary>
    public string Extension =>
        Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant();
}
