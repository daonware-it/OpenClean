#requires -Version 5.1
<#
.SYNOPSIS
    Prüft, dass in einem Publish-Ordner nichts Unsigniertes ausgeliefert wird (OPCL-20).

.DESCRIPTION
    Läuft in der Release-Pipeline NACH dem Signieren. Zwei Stufen:

    1. Pflichtdateien (-RequiredFiles): müssen existieren UND gültig von -ExpectedPublisher
       signiert sein. Diese Stufe ist der eigentliche Kern: Der Single-File-Publish-Ordner
       enthält praktisch nur OpenClean.exe, eine reine "nichts Unsigniertes gefunden"-Schleife
       würde auf dieser fast leeren Menge trivial bestehen.
    2. Alle übrigen PE-Dateien (*.exe, *.dll, *.sys, *.ocx): müssen gültig signiert sein –
       von -ExpectedPublisher oder (mit -AllowMicrosoft) von Microsoft. Greift automatisch,
       falls je vom Single-File- auf ein Layout mit losen DLLs umgestellt wird.

.PARAMETER Path
    Zu prüfender Ordner (z. B. publish\portable).

.PARAMETER ExpectedPublisher
    Erwarteter Signierer. Standard: DaonWare.

.PARAMETER RequiredFiles
    Dateien, die vorhanden UND korrekt signiert sein müssen. Standard: OpenClean.exe.

.PARAMETER AllowMicrosoft
    Erlaubt zusätzlich von Microsoft signierte PEs (nur relevant für Nicht-Single-File-Layouts,
    in denen die .NET-Runtime-DLLs lose danebenliegen).

.PARAMETER UseSignTool
    Verifiziert über "signtool verify /pa /all" statt Get-AuthenticodeSignature.
    In der CI empfohlen: Get-AuthenticodeSignature meldet UnknownError, wenn die Root-CA von
    Azure Trusted Signing (Microsoft Identity Verification Root CA 2020) auf dem Build-Agent
    fehlt – das wäre ein Fehlalarm, der den Release grundlos blockiert.

.OUTPUTS
    Exit-Code 0 = alles signiert · 1 = unsignierte/fremde PE gefunden ·
                2 = Pflichtdatei fehlt oder ist falsch signiert · 3 = Parameter-/Pfadfehler.

.EXAMPLE
    pwsh build\verify-signatures.ps1 -Path publish\portable -UseSignTool
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string]$ExpectedPublisher = 'DaonWare',

    [string[]]$RequiredFiles = @('OpenClean.exe'),

    [switch]$AllowMicrosoft,

    [switch]$UseSignTool
)

$ErrorActionPreference = 'Stop'

# Exit-Codes als sprechende Konstanten.
$EXIT_OK              = 0
$EXIT_UNSIGNED_FOUND  = 1
$EXIT_REQUIRED_FAILED = 2
$EXIT_BAD_ARGS        = 3

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    Write-Host "FEHLER: Ordner nicht gefunden: $Path" -ForegroundColor Red
    exit $EXIT_BAD_ARGS
}
$root = (Resolve-Path -LiteralPath $Path).Path

# signtool.exe im Windows SDK suchen (neuste Version zuerst).
function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

    foreach ($r in $roots) {
        $hit = Get-ChildItem -LiteralPath $r -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match '\\x64\\' } |
               Sort-Object FullName -Descending |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$signToolPath = $null
if ($UseSignTool) {
    $signToolPath = Find-SignTool
    if (-not $signToolPath) {
        Write-Host "FEHLER: -UseSignTool gesetzt, aber signtool.exe wurde nicht gefunden." -ForegroundColor Red
        exit $EXIT_BAD_ARGS
    }
    Write-Host "    signtool: $signToolPath"
}

<#
    Prüft eine Datei und liefert ein Ergebnisobjekt:
      Ok        - gültig signiert und vom erlaubten Herausgeber
      Status    - Kurztext für die Ausgabe
      Subject   - Zertifikats-Subject ("" wenn unbekannt)
      Timestamped - $true, wenn ein Gegenzeichnungs-Zeitstempel vorhanden ist
#>
function Test-Signature {
    param([string]$File)

    if ($signToolPath) {
        # signtool prüft die Kette selbst und kennt die Trusted-Signing-Roots.
        $output = & $signToolPath verify /pa /all $File 2>&1
        $valid  = ($LASTEXITCODE -eq 0)
        $text   = ($output | Out-String)

        # Subject aus der signtool-Ausgabe herausziehen ("Issued to: <Name>").
        $subject = ''
        if ($text -match 'Issued to:\s*(.+)') { $subject = $Matches[1].Trim() }

        $status = 'Invalid'
        if ($valid) { $status = 'Valid' }

        return [pscustomobject]@{
            Ok          = $valid -and (Test-Publisher -Subject $subject)
            Valid       = $valid
            Status      = $status
            Subject     = $subject
            Timestamped = ($text -match 'The signature is timestamped')
        }
    }

    $sig     = Get-AuthenticodeSignature -LiteralPath $File
    $valid   = ($sig.Status -eq 'Valid')
    $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '' }

    return [pscustomobject]@{
        Ok          = $valid -and (Test-Publisher -Subject $subject)
        Valid       = $valid
        Status      = [string]$sig.Status
        Subject     = $subject
        Timestamped = ($null -ne $sig.TimeStamperCertificate)
    }
}

# True, wenn das Subject vom erwarteten Herausgeber stammt (oder, mit -AllowMicrosoft, von Microsoft).
function Test-Publisher {
    param([string]$Subject)

    if ([string]::IsNullOrWhiteSpace($Subject)) { return $false }
    if ($Subject -like "*$ExpectedPublisher*") { return $true }
    if ($AllowMicrosoft -and $Subject -like '*Microsoft Corporation*') { return $true }
    return $false
}

Write-Host "==> Signaturprüfung" -ForegroundColor Cyan
Write-Host "    Ordner:     $root"
Write-Host "    Herausgeber: $ExpectedPublisher"

# ---- Stufe 1: Pflichtdateien --------------------------------------------------------
$requiredFailed = @()

foreach ($name in $RequiredFiles) {
    $file = Join-Path $root $name

    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        $requiredFailed += "$name : Datei fehlt im Publish-Ordner"
        continue
    }

    $r = Test-Signature -File $file

    if (-not $r.Valid) {
        $requiredFailed += "$name : nicht gültig signiert (Status: $($r.Status))"
        continue
    }
    if (-not $r.Ok) {
        $requiredFailed += "$name : fremder Herausgeber ($($r.Subject))"
        continue
    }

    # Ohne Gegenzeichnung wird die Signatur ungültig, sobald das Zertifikat abläuft.
    # Azure Trusted Signing stellt Zertifikate mit nur ~3 Tagen Laufzeit aus – ein
    # vergessener Zeitstempel macht den Release nach 72 Stunden unbrauchbar.
    if (-not $r.Timestamped) {
        Write-Host "    WARNUNG: $name ist ohne Zeitstempel signiert." -ForegroundColor Yellow
    }

    Write-Host "    OK  $name ($($r.Subject))" -ForegroundColor Green
}

if ($requiredFailed.Count -gt 0) {
    Write-Host ""
    Write-Host "FEHLER: Pflichtdateien nicht korrekt signiert:" -ForegroundColor Red
    $requiredFailed | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    exit $EXIT_REQUIRED_FAILED
}

# ---- Stufe 2: alle übrigen PE-Dateien -----------------------------------------------
$pes = Get-ChildItem -LiteralPath $root -Recurse -File -Include *.exe, *.dll, *.sys, *.ocx -ErrorAction SilentlyContinue

$bad     = @()
$counted = 0

foreach ($pe in $pes) {
    # Pflichtdateien sind oben schon geprüft.
    if ($RequiredFiles -contains $pe.Name -and $pe.DirectoryName -eq $root) { continue }

    $counted++
    $r = Test-Signature -File $pe.FullName

    if (-not $r.Ok) {
        $relative = $pe.FullName.Substring($root.Length).TrimStart('\')
        $reason   = if ($r.Valid) { "fremder Herausgeber ($($r.Subject))" } else { "Status: $($r.Status)" }
        $bad += "$relative : $reason"
    }
}

if ($bad.Count -gt 0) {
    Write-Host ""
    Write-Host "FEHLER: unsignierte oder fremd signierte Dateien gefunden:" -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    exit $EXIT_UNSIGNED_FOUND
}

Write-Host ""
Write-Host "==> Alle Signaturen in Ordnung ($($RequiredFiles.Count) Pflichtdatei(en), $counted weitere PE-Datei(en))." -ForegroundColor Green
exit $EXIT_OK
