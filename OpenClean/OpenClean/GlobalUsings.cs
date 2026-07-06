// Explizite globale Usings als echte Quelldatei.
//
// Ersetzt die auto-generierten ImplicitUsings (ImplicitUsings ist in der .csproj
// deaktiviert). Grund: JetBrains Rider las die generierte Datei
// obj/.../OpenClean.GlobalUsings.g.cs nicht ein und meldete dadurch hunderte
// Phantom-Fehler ("Cannot resolve symbol 'Environment'/'DateTime'/...").
// Als normale .cs-Datei wird sie von Riders Analyse garantiert erfasst.
//
// Inhalt entspricht exakt dem, was ImplicitUsings zuvor erzeugt hat.
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
global using OpenClean.Services.Localization;
