using OpenClean.Contracts;
using OpenClean.Services;
using OpenClean.Services.DriveHealth;
using OpenClean.Services.Licensing;
using OpenClean.Services.UI;

namespace OpenClean.ViewModels;

/// <summary>
/// Eine Karte der Datenträger-Gesundheit.
///
/// <para><b>Frei:</b> Ampel und Klartext – das reicht für die Frage „muss ich sichern?“.
/// <b>Pro:</b> die Einzelwerte im aufklappbaren Bereich; sie kommen aus dem geschlossenen
/// Premium-Modul (<see cref="ISmartDetailProvider"/>).</para>
///
/// <para>Die Details werden erst beim Aufklappen geholt, nicht beim Scan: die Dekodierung
/// kostet unnötig Zeit für Karten, die niemand öffnet.</para>
/// </summary>
public sealed class DriveHealthViewModel : ViewModelBase
{
    private bool _isExpanded;
    private SmartDetails? _details;
    private bool _detailsLoaded;

    private readonly IUiDispatcher _ui;

    public DriveHealthViewModel(DriveHealthInfo info, IUiDispatcher? ui = null)
    {
        _ui = ui ?? UiDispatcher.Default;

        Info = info;
        ToggleCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);

        // Upsell-Hinweis aktuell halten: kauft der Nutzer Pro während die Karte offen ist,
        // muss die Sperre sofort fallen (nicht erst nach App-Neustart).
        PremiumService.Instance.Changed += (_, _) =>
        {
            if (_ui.CheckAccess()) OnLicenseChanged();
            else _ui.Post(OnLicenseChanged);
        };
    }

    /// <summary>Die Rohdaten der Karte (Modell, Ampel, Kapazität, Buchstaben).</summary>
    public DriveHealthInfo Info { get; }

    public RelayCommand ToggleCommand { get; }

    /// <summary>True ohne Pro-Berechtigung: statt der Details erscheint der Upsell-Hinweis.</summary>
    public bool IsDetailLocked =>
        !PremiumService.Instance.HasFeature(PremiumContract.FeatureSmartDetails);

    /// <summary>Steuert den aufklappbaren Detailbereich. Lädt die Werte beim ersten Öffnen.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value) EnsureDetailsLoaded();
        }
    }

    /// <summary>Die Detailwerte – <c>null</c>, solange nicht geladen bzw. nicht verfügbar.</summary>
    public SmartDetails? Details
    {
        get => _details;
        private set
        {
            if (!SetProperty(ref _details, value)) return;
            OnPropertyChanged(nameof(HasDetails));
            OnPropertyChanged(nameof(TotalWrittenDisplay));
        }
    }

    public bool HasDetails => _details is not null;

    /// <summary>
    /// TBW als Text. Eigene Eigenschaft statt Konverter-Bindung: der gemeinsam genutzte
    /// Bytes-Konverter liefert für <c>null</c> einen leeren String, weshalb ein
    /// <c>TargetNullValue</c> nie greifen würde – die Zelle bliebe leer, während alle anderen
    /// Werte „—“ zeigen. Den Konverter zu ändern, würde andere Ansichten treffen.
    /// </summary>
    public string TotalWrittenDisplay =>
        Details?.TotalBytesWritten is long b ? ByteFormatter.Format(b) : "—";

    /// <summary>Text, wenn aufgeklappt wurde, aber keine Werte da sind (kein Modul, kein SMART).</summary>
    public string DetailsUnavailableText => Loc.T("system.health.unavailable");

    private void EnsureDetailsLoaded()
    {
        if (_detailsLoaded) return;
        // Ohne Pro-Lizenz wird das Modul gar nicht erst gefragt – und der Ladezustand bleibt
        // unberührt, damit ein späteres Aufklappen (nach Kauf) wirklich noch lädt.
        if (IsDetailLocked) return;

        _detailsLoaded = true;
        var module = PremiumService.Instance.Module;
        Details = ResolveDetails(
            Info,
            module as ISmartDetailProvider,
            module as INvmeDetailProvider);
    }

    /// <summary>
    /// Reaktion auf einen Lizenzwechsel: die Sperre neu melden und den Detailbereich
    /// angleichen. Upgrade (Kauf) → bei offener Karte gleich nachladen. Downgrade
    /// (Deaktivierung im laufenden Betrieb) → bereits geladene Werte verwerfen.
    /// </summary>
    private void OnLicenseChanged()
    {
        OnPropertyChanged(nameof(IsDetailLocked));

        if (IsDetailLocked)
        {
            // Ohne die Verwerfung bliebe eine offene Karte auf den gecachten Werten stehen
            // (HasDetails=true) und zeigte den Upsell-Hinweis GLEICHZEITIG mit den Pro-Werten –
            // ein nicht mehr Berechtigter sähe die Premium-Werte weiter. _detailsLoaded
            // zurücksetzen, damit ein späteres erneutes Aufklappen nach Neu-Kauf wieder lädt.
            _detailsLoaded = false;
            Details = null;
            return;
        }

        if (IsExpanded) EnsureDetailsLoaded();
    }

    /// <summary>
    /// Holt die Detailwerte. Liegt ein Blob vor, dekodiert ihn das Premium-Modul; ohne Modul
    /// (oder mit einem älteren, das die Fähigkeit nicht kennt) schlicht <c>null</c>. Ein Fehler
    /// im Modul darf die Karte nicht kippen – die Ampel bleibt davon unberührt sichtbar.
    ///
    /// <para><b>Ohne Blob</b> – der NVMe-Fall – gibt es nichts zu dekodieren, das Modul wird
    /// gar nicht erst gefragt. Die Werte aus dem Zuverlässigkeitszähler liegen aber bereits in
    /// <see cref="DriveHealthInfo"/>; sie hier zu verschweigen hieße, dem Pro-Nutzer auf seinem
    /// einzigen Laufwerk drei Striche zu zeigen, obwohl die Werte im Speicher stehen.</para>
    ///
    /// <para><b>Aufrufer beachten:</b> Dieser Pfad läuft ausschließlich hinter dem
    /// <see cref="IsDetailLocked"/>-Check in <c>EnsureDetailsLoaded</c>. Ohne Pro-Lizenz darf
    /// nichts davon passieren.</para>
    /// </summary>
    internal static SmartDetails? ResolveDetails(
        DriveHealthInfo info,
        ISmartDetailProvider? provider,
        INvmeDetailProvider? nvmeProvider = null)
    {
        // 1. NVMe: eigener Health-Log-Blob, eigenes Modul-Interface. Fehlt das Modul (älteres
        //    Modul / Ladefehler) oder wirft es, fallen wir auf die frei ermittelten
        //    Zuverlässigkeitszähler-Werte zurück – nie eine leere Karte, wenn Werte vorliegen.
        if (info.RawNvmeBlob is { Length: > 0 } nvmeBlob)
        {
            if (nvmeProvider is null) return FromKnownValues(info);
            try
            {
                return nvmeProvider.GetNvmeDetails(nvmeBlob, info.Model) ?? FromKnownValues(info);
            }
            catch
            {
                return FromKnownValues(info);
            }
        }

        // 2. ATA/SATA: roher SMART-Blob ans ATA-Modul. Ohne Modul bleibt es null („nicht
        //    verfügbar"), wie bisher – der Bestandskunden-Fall mit älterem Modul.
        if (info.RawSmartBlob is { Length: > 0 } blob)
        {
            if (provider is null) return null;
            try
            {
                return provider.GetDetails(blob, info.Model, info.MediaType);
            }
            catch
            {
                return null;
            }
        }

        // 3. Kein Blob (kein Passthrough, kein ATA): die bereits bekannten Zählerwerte.
        return FromKnownValues(info);
    }

    /// <summary>
    /// Baut die Details aus dem, was der Dienst ohne Blob bereits ermittelt hat. Nur, wenn
    /// mindestens ein Wert vorliegt – sonst bliebe eine Karte aus lauter Strichen übrig, für
    /// die der Hinweis „keine Detailwerte“ die ehrlichere Antwort ist.
    /// </summary>
    private static SmartDetails? FromKnownValues(DriveHealthInfo info)
    {
        bool hasAnyValue = info.TemperatureCelsius.HasValue || info.WearRemainingPercent.HasValue
                           || info.PowerOnHours.HasValue || info.PowerCycles.HasValue
                           || info.ReallocatedSectors.HasValue || info.PendingSectors.HasValue;

        if (!hasAnyValue) return null;

        return new SmartDetails(
            info.TemperatureCelsius,
            info.WearRemainingPercent,
            info.PowerOnHours,
            info.PowerCycles,   // aus StartStopCycleCount (z. B. USB-Platten ohne SMART/NVMe-Log)
            info.ReallocatedSectors,
            info.PendingSectors,
            null);              // TBW: nur aus NVMe-Log/ATA-Modul, bei HDD technisch nicht vorhanden
    }

    /// <summary>Nach Sprachwechsel die berechneten Texte neu anfordern.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(DetailsUnavailableText));
        OnPropertyChanged(nameof(IsDetailLocked));
    }
}
