using System;
using System.Collections.Generic;
using System.Windows;

namespace OpenClean.Contracts;

/// <summary>
/// Handshake zwischen der offenen App und dem geschlossenen Premium-Modul
/// (<c>OpenClean.Premium.dll</c>, ausgeliefert über daonware.de nach Lizenz-Aktivierung).
/// Bewusst minimal gehalten: das Modul referenziert die App-Assembly direkt und nutzt
/// deren Dienste (Loc, SettingsService, ScheduleTaskService, …); dieser Vertrag regelt
/// nur das Laden, die Versionsprüfung und die Übergabe der Lizenzdaten.
/// </summary>
public static class PremiumContract
{
    /// <summary>
    /// Vertragsversion. Wird bei JEDER inkompatiblen Änderung an diesem Projekt erhöht;
    /// der Loader lehnt Module mit abweichender Version ab (Hinweis auf Neu-Download).
    /// </summary>
    public const int Version = 1;

    /// <summary>Dateiname des Premium-Moduls im Datenverzeichnis (Unterordner <c>Modules\</c>).</summary>
    public const string ModuleFileName = "OpenClean.Premium.dll";

    /// <summary>Feature-Schlüssel: Geplante Reinigung (Zeitplan-Bereich + --auto-Lauf).</summary>
    public const string FeatureSchedule = "schedule";

    /// <summary>Feature-Schlüssel: Batch-Deinstallation (mehrere Programme auf einmal).</summary>
    public const string FeatureBatchUninstall = "batch-uninstall";
}

/// <summary>Verifizierte Lizenzdaten, die die App dem Modul beim Initialisieren übergibt.</summary>
public sealed record LicenseInfo(
    string KeyId,
    string Edition,
    IReadOnlyList<string> Features,
    DateTime ExpiresUtc);

/// <summary>Dienste, die die App dem Premium-Modul zur Verfügung stellt.</summary>
public interface IPremiumHost
{
    /// <summary>Die verifizierte Lizenz (Signatur wurde von der App bereits geprüft).</summary>
    LicenseInfo License { get; }

    /// <summary>Datenverzeichnis der App (portabel neben der EXE oder %AppData%\OpenClean).</summary>
    string DataDirectory { get; }
}

/// <summary>Ein vom Modul bereitgestellter UI-Bereich (heute: "Zeitplan").</summary>
public interface IPremiumSection
{
    /// <summary>Bereichs-Kennung; entspricht dem Enum-Namen von <c>AppSection</c> (z. B. "Zeitplan").</summary>
    string SectionId { get; }

    /// <summary>Erzeugt die View des Bereichs (inkl. eigenem DataContext).</summary>
    FrameworkElement CreateView();
}

/// <summary>
/// Fähigkeit: führt den unbeaufsichtigten geplanten Reinigungslauf (<c>--auto</c>) AUS.
/// Bewusst separat von <see cref="IPremiumModule"/>: Die gesamte Ausführungslogik liegt
/// ausschließlich im geschlossenen Modul – die offene App delegiert nur. Ohne geladenes,
/// signiertes und lizenzgebundenes Modul existiert schlicht kein Codepfad, der die geplante
/// Reinigung durchführt (Anti-Bypass: ein Patch der offenen Quelle findet nichts auszuführen).
/// </summary>
public interface IScheduledCleanupRunner
{
    /// <summary>
    /// Prüft Lizenz, Feature und Einstellungen SELBST, scannt, löscht, schreibt den Bericht
    /// und benachrichtigt. Liefert <c>true</c>, wenn ein Lauf ausgeführt wurde;
    /// <c>false</c>, wenn er abgelehnt wurde (keine gültige Lizenz bzw. Zeitplan-Feature).
    /// </summary>
    bool RunScheduledCleanup();
}

/// <summary>Einstiegspunkt des Premium-Moduls; genau eine öffentliche Implementierung pro Modul.</summary>
public interface IPremiumModule
{
    /// <summary>Muss <see cref="PremiumContract.Version"/> entsprechen, sonst wird das Modul nicht geladen.</summary>
    int ContractVersion { get; }

    /// <summary>Version des Moduls (Anzeige/Diagnose).</summary>
    string ModuleVersion { get; }

    /// <summary>Wird einmalig nach dem Laden aufgerufen, bevor <see cref="Sections"/> genutzt wird.</summary>
    void Initialize(IPremiumHost host);

    /// <summary>Die vom Modul bereitgestellten UI-Bereiche.</summary>
    IReadOnlyList<IPremiumSection> Sections { get; }
}
