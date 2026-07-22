using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using OpenClean.Models;
using OpenClean.ViewModels;

namespace OpenClean.Services.Dashboard;

/// <summary>
/// Aktions-Bündel für die Empfehlungs-Engine. Kapselt die (WPF-nahen) Nebenwirkungen –
/// Navigation, Bereinigungs-Scan, RAM-Aktualisierung – hinter neutralen Delegates/Commands,
/// damit <see cref="RecommendationBuilder"/> selbst ohne UI und testbar bleibt.
/// </summary>
public readonly struct RecommendationActions
{
    public RecommendationActions(Action<AppSection> navigate, ICommand cleanNow, ICommand refreshRam)
    {
        Navigate = navigate;
        CleanNow = cleanNow;
        RefreshRam = refreshRam;
    }

    /// <summary>Navigiert in den angegebenen App-Bereich.</summary>
    public Action<AppSection> Navigate { get; }

    /// <summary>Wechselt in die Bereinigung und stößt dort den Scan an.</summary>
    public ICommand CleanNow { get; }

    /// <summary>Aktualisiert die RAM-Anzeige (rein informativ, kein „Booster“).</summary>
    public ICommand RefreshRam { get; }
}

/// <summary>
/// Reine, WPF-freie Empfehlungs-Engine: leitet aus echten Messwerten
/// (<see cref="SystemMeasurements"/>) konkrete Handlungsempfehlungen ab.
///
/// <para>Herausgelöst aus <c>DashboardViewModel.BuildRecommendations</c>; Loc-Keys,
/// Dringlichkeiten, Arten, Sortierung und der „Alles gut“-Rückfall sind unverändert.
/// Die auslösenden Aktionen kommen über <see cref="RecommendationActions"/> herein –
/// kein <c>MessageBox</c>, <c>Application</c> oder <c>Dispatcher</c>.</para>
/// </summary>
public static class RecommendationBuilder
{
    /// <summary>Baut die Empfehlungsliste, dringlichste zuerst (Kritisch → Warnung → Hinweis).</summary>
    public static IReadOnlyList<Recommendation> Build(in SystemMeasurements m, RecommendationActions actions)
    {
        var list = new List<Recommendation>();

        double tempMb = m.TempBytes / (1024.0 * 1024);
        if (tempMb > 200)
        {
            var severity = tempMb > 1024 ? RecommendationSeverity.Warning : RecommendationSeverity.Info;
            list.Add(new Recommendation
            {
                Title = Loc.T("reco.reclaimable.title", ByteFormatter.Format(m.TempBytes)),
                Text = Loc.T("reco.reclaimable.text", m.TempItems),
                Severity = severity,
                Kind = RecommendationKind.Reclaimable,
                SeverityTag = SeverityTag(severity),
                ValueDisplay = ByteFormatter.Format(m.TempBytes),
                ValueUnit = Loc.T("reco.unit.reclaimable"),
                ActionLabel = Loc.T("common.clean"),
                ActionCommand = actions.CleanNow
            });
        }

        // Laufwerke mit kritisch/knapp wenig Speicher.
        foreach (var drive in m.Drives.Where(d => d.FreePercent < 15))
        {
            var severity = drive.FreePercent < 10 ? RecommendationSeverity.Critical : RecommendationSeverity.Warning;
            list.Add(new Recommendation
            {
                Title = Loc.T("reco.driveFull.title", drive.Letter, drive.UsedPercent.ToString("0")),
                Text = Loc.T("reco.driveFull.text", drive.FreeDisplay),
                Severity = severity,
                Kind = RecommendationKind.Storage,
                SeverityTag = SeverityTag(severity),
                ValueDisplay = drive.FreeDisplay,
                ValueUnit = Loc.T("reco.unit.free"),
                ActionLabel = Loc.T("reco.action.check"),
                ActionCommand = new RelayCommand(_ => actions.Navigate(AppSection.Bereinigung))
            });
        }

        if (m.StartupEnabled > 10 || m.StartupHighImpact > 0)
        {
            var severity = m.StartupHighImpact > 0 ? RecommendationSeverity.Warning : RecommendationSeverity.Info;
            list.Add(new Recommendation
            {
                Title = Loc.T("reco.startup.title", m.StartupEnabled),
                Text = Loc.T("reco.startup.text", m.StartupHighImpact),
                Severity = severity,
                Kind = RecommendationKind.Startup,
                SeverityTag = SeverityTag(severity),
                ValueDisplay = m.StartupEnabled.ToString(),
                ValueUnit = Loc.T("reco.unit.active"),
                ActionLabel = Loc.T("reco.action.view"),
                ActionCommand = new RelayCommand(_ => actions.Navigate(AppSection.Autostart))
            });
        }

        // RAM: rein informativ – KEINE „RAM-Booster“-Aktion (wäre Fake), nur Aktualisieren.
        if (m.RamValid && m.RamPercent > 85)
        {
            list.Add(new Recommendation
            {
                Title = Loc.T("reco.ram.title", m.RamPercent.ToString("0")),
                Text = Loc.T("reco.ram.text"),
                Severity = RecommendationSeverity.Info,
                Kind = RecommendationKind.Ram,
                SeverityTag = SeverityTag(RecommendationSeverity.Info),
                ValueDisplay = m.RamPercent.ToString("0") + " %",
                ValueUnit = Loc.T("reco.unit.load"),
                ActionLabel = Loc.T("common.refresh"),
                ActionCommand = actions.RefreshRam
            });
        }

        if (list.Count == 0)
        {
            list.Add(new Recommendation
            {
                Title = Loc.T("reco.allGood.title"),
                Text = Loc.T("reco.allGood.text"),
                Severity = RecommendationSeverity.Positive,
                Kind = RecommendationKind.AllGood,
                SeverityTag = SeverityTag(RecommendationSeverity.Positive)
            });
        }

        // Dringlichste zuerst (Kritisch → Warnung → Hinweis), Reihenfolge sonst stabil.
        return list.OrderByDescending(r => (int)r.Severity).ToList();
    }

    /// <summary>Lokalisierter Status-Tag je Dringlichkeit (Kritisch/Empfohlen/Hinweis/Optimal).</summary>
    private static string SeverityTag(RecommendationSeverity severity) => severity switch
    {
        RecommendationSeverity.Critical => Loc.T("reco.tag.critical"),
        RecommendationSeverity.Warning => Loc.T("reco.tag.recommended"),
        RecommendationSeverity.Info => Loc.T("reco.tag.hint"),
        _ => Loc.T("reco.tag.good")
    };
}
