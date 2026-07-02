#define MyAppName "Cobblemon Legacy Launcher"
#define MyAppVersion "1.3.1"
#define MyAppPublisher "Cobblemon Legacy"
#define MyAppExeName "CobblemonLegacy.exe"

[Setup]
AppId={{E91507EB-0D8C-42C8-9227-3DE36D2CF2E3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://www.cobblemonlegacy.com.br
AppSupportURL=https://discord.gg/sETS2Fc7Ey
AppUpdatesURL=https://gotardelo.github.io/cobblemonlegacy-downloads/
DefaultDirName={localappdata}\Programs\Cobblemon Legacy Launcher
DefaultGroupName=Cobblemon Legacy
OutputDir=..\dist
OutputBaseFilename=CobblemonLegacyLauncherSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
InfoBeforeFile=before-install.txt
SetupIconFile=..\Assets\launcher-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Cobblemon Legacy Launcher
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Cobblemon Legacy"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Cobblemon Legacy"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,Cobblemon Legacy}"; Flags: nowait postinstall skipifsilent
