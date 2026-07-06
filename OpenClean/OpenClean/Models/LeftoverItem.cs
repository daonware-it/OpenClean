using OpenClean.ViewModels;

namespace OpenClean.Models;

/// <summary>Art eines gefundenen Rests nach einer Deinstallation.</summary>
public enum LeftoverKind
{
    /// <summary>Ein zurückgebliebener Ordner (Datei-System).</summary>
    Folder,
    /// <summary>Ein zurückgebliebener Registry-Schlüssel.</summary>
    RegistryKey
}

/// <summary>
/// Ein nach einer Deinstallation zurückgebliebener Rest (Ordner oder Registry-Schlüssel).
/// Wird dem Nutzer zur PRÜFUNG angezeigt – gelöscht wird erst nach ausdrücklicher Bestätigung.
/// </summary>
public sealed class LeftoverItem : ViewModelBase
{
    /// <summary>Voller Pfad (Ordnerpfad bzw. Registry-Schlüsselpfad inkl. Hive-Präfix).</summary>
    public required string Path { get; init; }

    /// <summary>Ordner oder Registry-Schlüssel.</summary>
    public required LeftoverKind Kind { get; init; }

    /// <summary>Größe in Bytes (nur bei Ordnern sinnvoll; Registry = 0).</summary>
    public long SizeBytes { get; init; }

    private bool _isSelected = true;
    /// <summary>Ob dieser Rest beim Entfernen gelöscht wird.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsRegistry => Kind == LeftoverKind.RegistryKey;

    public string SizeDisplay => IsRegistry ? "" : Services.ByteFormatter.Format(SizeBytes);
}
