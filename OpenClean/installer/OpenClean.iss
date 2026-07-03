; Inno-Setup-Skript für OpenClean (v0.11.0).
; Baut einen klassischen Installer aus dem self-contained Publish-Verzeichnis.
; Die installierte App enthält KEINE OpenClean.portable-Marker-Datei und nutzt
; daher %AppData%\OpenClean für ihre Einstellungen (Portable-Modus nur beim .zip).
;
; Aufruf (siehe .github\workflows\release.yml):
;   ISCC.exe /DAppVersion=0.11.0 /DSourceDir=..\publish\portable installer\OpenClean.iss
;
; Hinweis: Der Installer ist (noch) unsigniert -> Windows SmartScreen kann warnen.
; Code-Signierung ist für v0.12.0 vorgesehen.

#ifndef AppVersion
  #define AppVersion "0.11.0"
#endif

#ifndef SourceDir
  ; Standard: relativ zu diesem .iss (installer\) das Portable-Publish-Verzeichnis.
  #define SourceDir "..\publish\portable"
#endif

#define AppName "OpenClean"
#define AppPublisher "OpenClean"
#define AppExeName "OpenClean.exe"
#define AppUrl "https://github.com/daonware-it/OpenClean"

[Setup]
AppId={{6F5C2E1A-3B2D-4C7E-9A1F-0C1EA4110ABC}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installation nach Program Files erfordert Adminrechte (passt zum UAC-Manifest der App).
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
OutputDir=Output
OutputBaseFilename=OpenClean-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Gesamtes Publish-Verzeichnis übernehmen, aber Portable-Marker und Debug-Symbole
; ausschließen (Marker würde sonst den Portable-Modus in einer Installation aktivieren).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "OpenClean.portable,*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
