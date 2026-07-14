using System.IO;
using OpenClean.Models;
using OpenClean.Services;

namespace OpenClean.ViewModels;

/// <summary>Eine Zeile in der Große-Dateien-Liste.</summary>
public sealed class LargeFileViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _hasError;

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

    /// <summary>True, wenn das Verschieben in den Papierkorb fehlgeschlagen ist.</summary>
    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    /// <summary>Markiert die Zeile als fehlgeschlagen – sie bleibt sichtbar stehen,
    /// statt still zu verschwinden.</summary>
    public void MarkFailed()
    {
        HasError = true;
        IsSelected = false;
    }

    /// <summary>Neu auswerten der berechneten Texte nach einem Sprachwechsel.</summary>
    public void Relocalize() => OnPropertyChanged(nameof(DateDisplay));
}
