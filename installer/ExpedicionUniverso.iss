; Instalador de Expedición Universo para Windows (Inno Setup 6/7).
; Compilar:  crear_instalador.cmd   (o: ISCC.exe ExpedicionUniverso.iss)
; Requiere antes exportar el juego a ..\exports\windows (preset "Windows" de Godot).

#define MyAppName "Expedición Universo"
#define MyAppVersion "1.0"
#define MyAppExe "ExpedicionUniverso.exe"

[Setup]
AppId={{5B03A288-5D70-4858-94E5-CE0E1CA3C08F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Expedición Universo
DefaultDirName={autopf}\Expedicion Universo
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\exports
OutputBaseFilename=ExpedicionUniverso_Setup_{#MyAppVersion}
SetupIconFile=..\game\icon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\exports\windows\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\exports\windows\data_AresX_windows_x86_64\*"; DestDir: "{app}\data_AresX_windows_x86_64"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LEEME.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autoprograms}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
