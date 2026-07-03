#requires -Version 5.1
<#
.SYNOPSIS
    Baut die Portable-Version von OpenClean (self-contained Single-File-EXE) und
    legt die Portable-Marker-Datei daneben.

.DESCRIPTION
    Nutzt das Publish-Profil "Portable" (OpenClean\Properties\PublishProfiles\Portable.pubxml).
    Ergebnis liegt in <Repo>\publish\portable und läuft sofort im Portable-Modus
    (Einstellungen/Protokolle landen dann im Unterordner Data\ neben der EXE).

.PARAMETER OutDir
    Zielverzeichnis. Standard: <Repo>\publish\portable

.EXAMPLE
    pwsh build\publish-portable.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

# Repo-Wurzel = Elternordner dieses Skripts (build\ liegt direkt im Repo-Root).
$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot 'OpenClean\OpenClean.csproj'

if (-not $OutDir) {
    $OutDir = Join-Path $repoRoot 'publish\portable'
}

Write-Host "==> Portable-Build von OpenClean" -ForegroundColor Cyan
Write-Host "    Projekt: $project"
Write-Host "    Ausgabe: $OutDir"

# Sauberes Zielverzeichnis
if (Test-Path $OutDir) {
    Remove-Item -Recurse -Force $OutDir
}

dotnet publish $project -p:PublishProfile=Portable -o $OutDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish ist fehlgeschlagen (Exit-Code $LASTEXITCODE)."
}

# Portable-Marker: aktiviert beim Start den Portable-Modus (Config neben der EXE).
$marker = Join-Path $OutDir 'OpenClean.portable'
Set-Content -Path $marker -Value 'Diese Datei aktiviert den Portable-Modus von OpenClean. Nicht loeschen.' -Encoding UTF8

Write-Host "==> Fertig. Portable-EXE + Marker liegen in:" -ForegroundColor Green
Write-Host "    $OutDir"
