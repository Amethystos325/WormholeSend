; The Windows installer. Built by build.ps1, which publishes the app to
; ..\build\publish and passes AppVersion; the setup lands in ..\build.

#ifndef AppVersion
  #error Build with build.ps1, which sets AppVersion
#endif

#define AppName "Wormhole Send"
#define AppExe "WormholeSend.exe"
; The name the app gives its own Run value (Autostart.cs), so the switch in
; the settings and this installer's task are the same thing.
#define RunValue "Wormhole Send"

[Setup]
AppId={{7555F7ED-8057-4EA7-9B9A-141B9CDE0D53}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Amethystos325
AppPublisherURL=https://github.com/Amethystos325/WormholeSend
AppSupportURL=https://github.com/Amethystos325/WormholeSend/issues
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; Admin, for the firewall rules.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\..\..\assets\app-icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
ShowLanguageDialog=auto
; The app lives in the tray: close it to replace its files. Not restarted
; by Setup, which would run it as administrator; the last page offers to
; start it as the user.
CloseApplications=force
RestartApplications=no
OutputDir=..\build
OutputBaseFilename=WormholeSend-{#AppVersion}-setup-x64
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
; Inno Setup ships Chinese as an unofficial translation, so it is kept here.
Name: "zh"; MessagesFile: "ChineseSimplified.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
en.Autostart=Start with Windows
zh.Autostart=开机时启动
ja.Autostart=Windows の起動時に開始
en.Firewall=Letting {#AppName} through the firewall...
zh.Firewall=正在让 {#AppName} 通过防火墙...
ja.Firewall={#AppName} をファイアウォールで許可しています...

[Tasks]
Name: "autostart"; Description: "{cm:Autostart}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\build\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Uninstalling removes the value whether this task or the settings set it
; (see CurUninstallStepChanged).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValue}"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart

[Run]
; Inbound on private networks, for this exe only: TCP 53320 transfers and
; 53321 control (core/src/config.rs DEFAULT_PORT and the next one), UDP 53317
; multicast discovery (MULTICAST_PORT, LocalSend's). Deleted first so a
; reinstall doesn't add them twice.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName} (TCP-In)"""; Flags: runhidden; StatusMsg: "{cm:Firewall}"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName} (UDP-In)"""; Flags: runhidden; StatusMsg: "{cm:Firewall}"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName} (TCP-In)"" dir=in action=allow profile=private protocol=TCP localport=53320,53321 program=""{app}\{#AppExe}"""; Flags: runhidden; StatusMsg: "{cm:Firewall}"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName} (UDP-In)"" dir=in action=allow profile=private protocol=UDP localport=53317 program=""{app}\{#AppExe}"""; Flags: runhidden; StatusMsg: "{cm:Firewall}"
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName} (TCP-In)"""; Flags: runhidden; RunOnceId: "FirewallTcp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName} (UDP-In)"""; Flags: runhidden; RunOnceId: "FirewallUdp"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Quit the app if it runs from here: it sits in the tray, and its
    // files couldn't be removed.
    Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "Get-Process WormholeSend -ErrorAction SilentlyContinue | ' +
      'Where-Object { $_.Path -like ''' + ExpandConstant('{app}') + '\*'' } | Stop-Process -Force"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // Start with Windows, if it points here (the settings may have set it).
    if RegQueryStringValue(HKCU, RunKey, '{#RunValue}', Command) and
       (Pos(Lowercase(ExpandConstant('{app}\{#AppExe}')), Lowercase(Command)) > 0) then
      RegDeleteValue(HKCU, RunKey, '{#RunValue}');
  end;
end;
