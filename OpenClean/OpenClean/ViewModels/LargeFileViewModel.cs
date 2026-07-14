using System.IO;
using OpenClean.Models;
using OpenClean.Services;

namespace OpenClean.ViewModels;

/// <summary>Eine Zeile in der Große-Dateien-Liste.</summary>
public sealed class LargeFileViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _hasError;
    private bool _isProtected;

    public LargeFileViewModel(LargeFile file)
    {
        File = file;
    }

    public LargeFile File { get; }

    public string FullPath => File.FullPath;
    public string FileName => Path.GetFileName(File.FullPath);

    /// <summary>Ordner der Datei – in der Liste unter dem Namen angezeigt.</summary>
    public string FolderPath => Path.GetDirectoryName(File.FullPath) ?? "";

    public long SizeBytes => File.SizeBytes;
    public DateTime LastWriteTime => File.LastWriteTime;

    public string SizeDisplay => ByteFormatter.Format(File.SizeBytes);
    public string DateDisplay => File.LastWriteTime.ToString("d");

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                SelectionChanged?.Invoke();
        }
    }

    /// <summary>Wird vom übergeordneten ViewModel gesetzt, um die Summen zu aktualisieren.</summary>
    public Action? SelectionChanged { get; set; }

    /// <summary>True, wenn das Verschieben in den Papierkorb fehlgeschlagen ist (gesperrt,
    /// keine Rechte o. Ä.) – <see cref="IsProtected"/> ist der eigene, davon getrennte Fall.</summary>
    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    /// <summary>True, wenn <see cref="PathSafety"/> die Datei als geschützten Systempfad
    /// blockiert hat, bevor überhaupt ein Löschversuch stattfand. Eigener Zustand statt
    /// <see cref="HasError"/>, damit die Zeile die zutreffende Meldung zeigt
    /// („largefiles.protected" statt der irreführenden „largefiles.deleteFailed").</summary>
    public bool IsProtected
    {
        get => _isProtected;
        private set => SetProperty(ref _isProtected, value);
    }

    /// <summary>Markiert die Zeile als fehlgeschlagen – sie bleibt sichtbar stehen,
    /// statt still zu verschwinden.</summary>
    public void MarkFailed()
    {
        HasError = true;
        IsSelected = false;
    }

    /// <summary>Markiert die Zeile als von PathSafety blockiert (geschützter Systempfad) –
    /// kein Löschversuch fand statt, das ist kein „Fehler" im Sinn von <see cref="HasError"/>.</summary>
    public void MarkProtected()
    {
        IsProtected = true;
        IsSelected = false;
    }

    /// <summary>Neu auswerten der berechneten Texte nach einem Sprachwechsel.</summary>
    public void Relocalize() => OnPropertyChanged(nameof(DateDisplay));
}
