using System;
using System.Collections.Generic;
using OpenClean.Models;

namespace OpenClean.Services.Dashboard;

/// <summary>
/// Reine, WPF-freie Score-Engine: leitet aus echten Messwerten (<see cref="SystemMeasurements"/>)
/// deterministisch einen Gesamt-Score von 0–100 samt nachvollziehbaren Einzel-Faktoren ab.
/// NICHTS wird beschönigt – jeder Abzug entspricht einem echten Signal.
///
/// <para>Herausgelöst aus <c>DashboardViewModel.ComputeScore</c>; Schwellen, Punkte,
/// Reihenfolge und Loc-Keys sind unverändert übernommen.</para>
/// </summary>
public static class SystemScoreCalculator
{
    /// <summary>Berechnet den Score inkl. aller mindernden Faktoren aus den Roh-Messwerten.</summary>
    public static ScoreResult Compute(in SystemMeasurements m)
    {
        int score = 100;
        var factors = new List<ScoreFactor>();

        void Penalty(int points, ScoreFactorKind kind, string description)
        {
            if (points <= 0) return;
            score -= points;
            factors.Add(new ScoreFactor(description, -points, kind));
        }

        // 1) Speicherplatz je Laufwerk (echte DriveInfo-Werte).
        foreach (var drive in m.Drives)
        {
            double freePct = drive.FreePercent;
            if (freePct < 10)
                Penalty(25, ScoreFactorKind.Storage, Loc.T("score.factor.driveCritical", drive.Letter, freePct.ToString("0")));
            else if (freePct < 20)
                Penalty(10, ScoreFactorKind.Storage, Loc.T("score.factor.driveLow", drive.Letter, freePct.ToString("0")));
        }

        // 2) Bereinigbare Temp-/Cache-Größe (echtes ScanAll-Ergebnis).
        double tempGb = m.TempBytes / (1024.0 * 1024 * 1024);
        double tempMb = m.TempBytes / (1024.0 * 1024);
        if (tempGb > 5)
            Penalty(20, ScoreFactorKind.Reclaimable, Loc.T("score.factor.reclaimable", ByteFormatter.Format(m.TempBytes)));
        else if (tempGb > 1)
            Penalty(10, ScoreFactorKind.Reclaimable, Loc.T("score.factor.reclaimable", ByteFormatter.Format(m.TempBytes)));
        else if (tempMb > 200)
            Penalty(5, ScoreFactorKind.Reclaimable, Loc.T("score.factor.reclaimable", ByteFormatter.Format(m.TempBytes)));

        // 3) Autostart (echte StartupService-Daten).
        if (m.StartupEnabled > 20)
            Penalty(15, ScoreFactorKind.Startup, Loc.T("score.factor.startupActive", m.StartupEnabled));
        else if (m.StartupEnabled > 10)
            Penalty(8, ScoreFactorKind.Startup, Loc.T("score.factor.startupActive", m.StartupEnabled));

        if (m.StartupHighImpact > 0)
        {
            int highPenalty = Math.Min(m.StartupHighImpact * 3, 12);
            Penalty(highPenalty, ScoreFactorKind.Startup, Loc.T("score.factor.startupHigh", m.StartupHighImpact));
        }

        // 4) RAM-Auslastung (echtes dwMemoryLoad).
        if (m.RamValid)
        {
            if (m.RamPercent > 90)
                Penalty(15, ScoreFactorKind.Ram, Loc.T("score.factor.ramLoad", m.RamPercent.ToString("0")));
            else if (m.RamPercent > 80)
                Penalty(8, ScoreFactorKind.Ram, Loc.T("score.factor.ramLoad", m.RamPercent.ToString("0")));
        }

        score = Math.Clamp(score, 0, 100);

        var (level, label) = score >= 80
            ? (ScoreLevel.Good, Loc.T("score.good"))
            : score >= 50
                ? (ScoreLevel.Improvable, Loc.T("score.improvable"))
                : (ScoreLevel.Critical, Loc.T("score.critical"));

        return new ScoreResult
        {
            Score = score,
            Level = level,
            Label = label,
            Factors = factors
        };
    }
}
