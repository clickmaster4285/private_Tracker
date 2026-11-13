; Inno Setup script for Alpha AI Tracker
; Produces a single .exe installer with wizard UI
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)

; Generated app identifiers from APP_IDENTIFIERS
#include "windows\windows_vars.iss"

#define MyAppAssocName MyAppName + " File"
#define MyAppAssocExt ".myp"
#define MyAppAssocKey StringChange(MyAppAssocName, " ", "") + MyAppAssocExt

; Read version from VERSION file (single source of truth)
#define VERSION_FILE FileOpen("..\\VERSION")
#define MyAppVersion FileRead(VERSION_FILE)
#undef VERSION_FILE

[Setup]
AppId={{B0A0A0A0-1A2B-3C4D-5E6F-7A8B9C0D1E2F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
ChangesAssociations=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\installers
OutputBaseFilename=AlphaAITracker-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupIconFile=avalonia-logo.ico
CloseApplications=force
AppMutex={#APP_MUTEX}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CreateUninstallRegKey=yes
; Force silent kill before any install steps

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]
Source: "windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Platform\Linux\*;Service\*;redist\*"
; restartreplace: if a handle still holds Service\*.dll after stop attempts, schedule
; replace on reboot instead of failing the whole upgrade with DeleteFile code 5.
Source: "windows\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace; Check: DirExists(ExpandConstant('{src}\windows\Service'))
Source: "windows\config.enc"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist; Check: FileExists(ExpandConstant('{src}\windows\config.enc'))
Source: "..\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion
; Optional full VC++ redist (ALPHA_BUNDLE_VC_REDIST=1). Check: is install-time only —
; missing Source still fails compile, so gate with ISPP FileExists.
#if FileExists(AddBackslash(SourcePath) + "windows\redist\vc_redist.x64.exe")
Source: "windows\redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
#endif

[Registry]
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocExt}\OpenWithProgids"; ValueType: string; ValueName: "{#MyAppAssocKey}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}"; ValueType: string; ValueName: ""; ValueData: "{#MyAppAssocName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Install VC++ CRT system-wide when the optional redistributable was bundled.
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Visual C++ runtime..."; Flags: waituntilterminated; Check: FileExists(ExpandConstant('{tmp}\vc_redist.x64.exe'))
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]

// ────────────────────────────────────────────────
// Upgrade / install must unlock Service\*.dll BEFORE file copy.
// Root cause of DeleteFile code 5: LocalSystem DLP service keeps
// AlphaAITracker.Service.dll mapped; admin "sc stop" is denied by locked DACL,
// so we stop/kill/delete via a short SYSTEM scheduled task (TEMP script — not
// ProgramData\dlp, which can be ACL-denied).
//
// ⚠️ NEVER pass taskkill /T (tree-kill). Self-updater is a descendant of the app.
// ────────────────────────────────────────────────

var
  GInstallSucceeded: Boolean;
  GServiceWasStoppedForUpgrade: Boolean;
  GNeedsRestart: Boolean;

procedure RestoreUsbStorage;
var
  ResultCode: Integer;
begin
  // DLP sets USBSTOR Start=4 (disabled). Must restore Start=3 (manual) on uninstall
  // or USB pendrives stay blocked after the service is gone.
  Exec('reg.exe',
    'add "HKLM\SYSTEM\CurrentControlSet\Services\USBSTOR" /v Start /t REG_DWORD /d 3 /f',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'config USBSTOR start= demand', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Strip AlphaAI-DLP hosts block + firewall rules left by DomainBlockEnforcer.
// Protected-uninstall also does this; Inno covers Apps & features uninstall path.
procedure ClearDlpWebBlock;
var
  ResultCode: Integer;
  ScriptDir, ScriptPath: string;
begin
  ScriptDir := ExpandConstant('{tmp}');
  ForceDirectories(ScriptDir);
  ScriptPath := ScriptDir + '\teardown-clear-hosts.ps1';
  SaveStringToFile(ScriptPath,
    '$ErrorActionPreference=''SilentlyContinue'''#13#10 +
    '$p=Join-Path $env:SystemRoot ''System32\drivers\etc\hosts'''#13#10 +
    'if (Test-Path $p) {'#13#10 +
    '  $c=Get-Content -LiteralPath $p -Raw'#13#10 +
    '  if ($null -ne $c -and $c -match ''AlphaAI-DLP'') {'#13#10 +
    '    $lines=$c -replace "`r`n","`n" -replace "`r","`n" -split "`n"'#13#10 +
    '    $kept=New-Object System.Collections.Generic.List[string]'#13#10 +
    '    $in=$false'#13#10 +
    '    foreach ($line in $lines) {'#13#10 +
    '      $t=$line.Trim()'#13#10 +
    '      if ($t -eq ''# AlphaAI-DLP-BEGIN'') { $in=$true; continue }'#13#10 +
    '      if ($t -eq ''# AlphaAI-DLP-END'') { $in=$false; continue }'#13#10 +
    '      if ($in) { continue }'#13#10 +
    '      if ($t -like ''*# AlphaAI-DLP*'') { continue }'#13#10 +
    '      [void]$kept.Add($line)'#13#10 +
    '    }'#13#10 +
    '    while ($kept.Count -gt 0 -and [string]::IsNullOrWhiteSpace($kept[$kept.Count-1])) { $kept.RemoveAt($kept.Count-1) }'#13#10 +
    '    $out=($kept -join "`r`n")'#13#10 +
    '    if ($out.Length -gt 0) { $out += "`r`n" }'#13#10 +
    '    [System.IO.File]::WriteAllText($p, $out, (New-Object System.Text.UTF8Encoding $false))'#13#10 +
    '  }'#13#10 +
    '}'#13#10 +
    '$out = netsh advfirewall firewall show rule name=all 2>$null'#13#10 +
    'foreach ($m in [regex]::Matches([string]$out, ''(?m)^Rule Name:\s*(.+?)\s*$'')) {'#13#10 +
    '  $n=$m.Groups[1].Value.Trim()'#13#10 +
    '  if ($n.StartsWith(''AlphaAI-DLP-'')) { netsh advfirewall firewall delete rule name="$n" | Out-Null }'#13#10 +
    '}'#13#10 +
    'ipconfig /flushdns | Out-Null'#13#10,
    False);
  Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function ProcessExists(const ExeName: string): Boolean;
var
  ResultCode: Integer;
begin
  // find returns 0 when the image name is present in tasklist output.
  Result :=
    Exec('cmd.exe',
      '/C tasklist /FI "IMAGENAME eq ' + ExeName + '" | find /I "' + ExeName + '" >nul',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function ServiceDllUnlocked: Boolean;
var
  DllPath: string;
begin
  DllPath := ExpandConstant('{app}\Service\AlphaAITracker.Service.dll');
  if not FileExists(DllPath) then
  begin
    Result := True;
    Exit;
  end;
  // If we can rename aside and back, no process holds a mandatory lock.
  if RenameFile(DllPath, DllPath + '.upgrade-test') then
  begin
    RenameFile(DllPath + '.upgrade-test', DllPath);
    Result := True;
  end
  else
    Result := False;
end;

procedure RunSystemCmd(const CmdLine: string);
var
  ResultCode: Integer;
  Script: string;
  Task: string;
begin
  Script := ExpandConstant('{tmp}\aat-svc-upgrade.cmd');
  SaveStringToFile(Script,
    '@echo off' + #13#10 +
    CmdLine + #13#10, False);
  Task := '{#WINDOWS_SERVICE_MAINT_TASK}';
  Exec('schtasks.exe',
    '/Create /TN "' + Task + '" /TR "cmd.exe /c \"' + Script + '\"" /SC ONCE /ST 00:00 /RU SYSTEM /F',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('schtasks.exe', '/Run /TN "' + Task + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('ping', '127.0.0.1 -n 4 -w 1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('schtasks.exe', '/Delete /TN "' + Task + '" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Upgrade path: stop + kill + delete service registration so Service\*.dll unlocks.
// Does NOT clear hosts (avoids ProgramData ACL / long TR failures that used to skip stop).
procedure StopDeleteDlpServiceForUpgrade;
var
  ResultCode: Integer;
  Attempt: Integer;
begin
  GServiceWasStoppedForUpgrade := True;
  for Attempt := 1 to 3 do
  begin
    // Clear failure actions first (best-effort as admin; SYSTEM script repeats).
    Exec('sc.exe', 'failure {#WINDOWS_SERVICE_NAME} reset= 0 actions= //', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RunSystemCmd(
      'sc.exe failure {#WINDOWS_SERVICE_NAME} reset= 0 actions= // >nul 2>&1 & ' +
      'sc.exe stop {#WINDOWS_SERVICE_NAME} >nul 2>&1 & ' +
      'ping 127.0.0.1 -n 3 -w 1000 >nul & ' +
      'taskkill /F /IM {#WINDOWS_SERVICE_EXE_NAME} >nul 2>&1 & ' +
      'sc.exe delete {#WINDOWS_SERVICE_NAME} >nul 2>&1 & ' +
      'taskkill /F /IM {#WINDOWS_SERVICE_EXE_NAME} >nul 2>&1');
    Exec('taskkill', '/F /IM {#WINDOWS_SERVICE_EXE_NAME}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('ping', '127.0.0.1 -n 3 -w 1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not ProcessExists('{#WINDOWS_SERVICE_EXE_NAME}') then
      Break;
  end;
end;

procedure StopDeleteDlpServiceAsSystem;
begin
  // Full uninstall path: hosts/USB cleanup + service stop.
  ClearDlpWebBlock;
  RestoreUsbStorage;
  StopDeleteDlpServiceForUpgrade;
  ClearDlpWebBlock;
end;

function KillRunningInstance: Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  // Kill tracker FIRST so a stuck --protected-uninstall cannot lock {app}\*.*
  // Kill by image name ONLY (no /T — see warning above).
  Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  StopDeleteDlpServiceForUpgrade;
  Exec('taskkill', '/F /IM {#WINDOWS_SERVICE_EXE_NAME}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('ping', '127.0.0.1 -n 3 -w 1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure LockDlpServiceAcl;
var
  ResultCode: Integer;
begin
  // SY=full; BA=query only — DLP service cannot be stopped from services.msc.
  Exec('sc.exe',
    'sdset {#WINDOWS_SERVICE_NAME} D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWLOCRRC;;;BA)',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Upsert Key=Value — build-baked knobs from client/.env (via windows_vars.iss)
// must win on every install/upgrade. Never touches ALPHA_SERVER_URL / device tokens.
procedure SetEnvKey(const EnvPath, Key, Value: string);
var
  Content: AnsiString;
  Needle, NewLine: string;
  P, LineStart, LineEnd: Integer;
begin
  NewLine := Key + '=' + Value + #13#10;
  Needle := Key + '=';
  if not FileExists(EnvPath) then
  begin
    SaveStringToFile(EnvPath, NewLine, False);
    Exit;
  end;
  if not LoadStringFromFile(EnvPath, Content) then
    Exit;
  P := Pos(Needle, Content);
  if P = 0 then
  begin
    if (Length(Content) > 0) and (Content[Length(Content)] <> #10) then
      Content := Content + #13#10;
    Content := Content + NewLine;
    SaveStringToFile(EnvPath, Content, False);
    Exit;
  end;
  LineStart := P;
  LineEnd := P;
  while (LineEnd <= Length(Content)) and (Content[LineEnd] <> #10) do
    Inc(LineEnd);
  if LineEnd <= Length(Content) then
    Inc(LineEnd); // include LF
  Delete(Content, LineStart, LineEnd - LineStart);
  Insert(NewLine, Content, LineStart);
  SaveStringToFile(EnvPath, Content, False);
end;

procedure EnsureServiceEnvFile;
var
  DataDir: string;
  EnvPath: string;
begin
  DataDir := ExpandConstant('{commonappdata}\{#APP_MUTEX}\dlp');
  ForceDirectories(DataDir);
  EnvPath := DataDir + '\service.env';
  // Values come from client/.env at build (generate-windows-vars.sh → #defines).
  SetEnvKey(EnvPath, 'ALPHA_DLP_ENABLED', '{#ALPHA_DLP_ENABLED}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_USB_BLOCK', '{#ALPHA_DLP_USB_BLOCK}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_WEB_BLOCK', '{#ALPHA_DLP_WEB_BLOCK}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_NETWORK_LOCKDOWN', '{#ALPHA_DLP_NETWORK_LOCKDOWN}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_APP_BLOCK', '{#ALPHA_DLP_APP_BLOCK}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_PORT_BLOCK', '{#ALPHA_DLP_PORT_BLOCK}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_DELETE_BLOCK', '{#ALPHA_DLP_DELETE_BLOCK}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_LOGIN_REBOOT', '{#ALPHA_DLP_LOGIN_REBOOT}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_POLICY_POLL_SEC', '{#ALPHA_DLP_POLICY_POLL_SEC}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_ALERT_SYNC_SEC', '{#ALPHA_DLP_ALERT_SYNC_SEC}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_PIPE_NAME', '{#WINDOWS_DLP_PIPE_NAME}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_WATCHDOG', '{#ALPHA_DLP_WATCHDOG}');
  SetEnvKey(EnvPath, 'ALPHA_DLP_WATCHDOG_POLL_SEC', '{#ALPHA_DLP_WATCHDOG_POLL_SEC}');
  SetEnvKey(EnvPath, 'ALPHA_UPDATE_ENABLED', '{#ALPHA_UPDATE_ENABLED}');
  SetEnvKey(EnvPath, 'ALPHA_UPDATE_AUTO_INSTALL', '{#ALPHA_UPDATE_AUTO_INSTALL}');
  SetEnvKey(EnvPath, 'ALPHA_UPDATE_CHECK_INTERVAL_SEC', '{#ALPHA_UPDATE_CHECK_INTERVAL_SEC}');
  SetEnvKey(EnvPath, 'ALPHA_UPDATE_DOWNLOAD_JITTER_SEC', '{#ALPHA_UPDATE_DOWNLOAD_JITTER_SEC}');
end;

// Private-LAN HTTPS: copy baked tls\rootCA.pem → ProgramData and LocalMachine\Root.
// Runs elevated (PrivilegesRequired=admin). Idempotent on upgrade / silent update.
// No employee action — same CA that signed the API leaf must be present at bake time
// (build-installer.sh copies repo certs\rootCA.pem into publish\windows\tls\).
procedure InstallOrgTlsCa;
var
  Src, DestDir, Dest: string;
  ResultCode: Integer;
begin
  Src := ExpandConstant('{app}\tls\rootCA.pem');
  if not FileExists(Src) then
    Exit;

  DestDir := ExpandConstant('{commonappdata}\{#APP_MUTEX}\tls');
  ForceDirectories(DestDir);
  Dest := DestDir + '\rootCA.pem';
  if not CopyFile(Src, Dest, False) then
    Exit;

  // Prefer certutil (PEM). Non-zero exit is non-fatal — TlsCertificateTrust still
  // loads the ProgramData PEM as ExtraStore for HttpClient/WebSocket.
  Exec('certutil.exe', '-addstore -f Root "' + Dest + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure InstallDlpService;
var
  ResultCode: Integer;
  ExePath: string;
begin
  ExePath := ExpandConstant('{app}\Service\{#WINDOWS_SERVICE_EXE_NAME}');
  if not FileExists(ExePath) then
    Exit;

  EnsureServiceEnvFile;

  // Ensure no stale registration before create (upgrade already deleted; rollback may need recreate).
  Exec('sc.exe', 'stop {#WINDOWS_SERVICE_NAME}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'delete {#WINDOWS_SERVICE_NAME}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('ping', '127.0.0.1 -n 2 -w 1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('sc.exe',
    'create {#WINDOWS_SERVICE_NAME} binPath= "' + ExePath + '" start= auto DisplayName= "{#WINDOWS_SERVICE_DISPLAY_NAME}" obj= LocalSystem',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe',
    'description {#WINDOWS_SERVICE_NAME} "{#MyAppName} Security & DLP enforcement (USB / policy / alerts)."',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe',
    'failure {#WINDOWS_SERVICE_NAME} reset= 86400 actions= restart/5000/restart/10000/restart/30000',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'start {#WINDOWS_SERVICE_NAME}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  LockDlpServiceAcl;
end;

procedure RemoveDlpService;
begin
  // Only reached after teardown.ok (removal code). Prefer SYSTEM maintenance task.
  ClearDlpWebBlock;
  RestoreUsbStorage;
  StopDeleteDlpServiceAsSystem;
end;

// ────────────────────────────────────────────────
// InitializeSetup — do NOT stop/delete DLP yet (user may cancel the wizard).
// Only free the tracker UI process so AppMutex does not block setup.
// ────────────────────────────────────────────────
function InitializeSetup: Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  GInstallSucceeded := False;
  GServiceWasStoppedForUpgrade := False;
  GNeedsRestart := False;
  Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Runs immediately before files are installed — MUST unlock Service\*.dll.
// Non-empty Result aborts setup BEFORE file copy (Inno changes nothing; we
// re-start the service in DeinitializeSetup if we had stopped it).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Attempt: Integer;
  ResultCode: Integer;
begin
  NeedsRestart := False;
  GNeedsRestart := False;
  KillRunningInstance;

  for Attempt := 1 to 5 do
  begin
    if (not ProcessExists('{#WINDOWS_SERVICE_EXE_NAME}')) and ServiceDllUnlocked then
    begin
      Result := '';
      Exit;
    end;
    StopDeleteDlpServiceForUpgrade;
    Exec('ping', '127.0.0.1 -n 2 -w 1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if not ProcessExists('{#WINDOWS_SERVICE_EXE_NAME}') then
  begin
    // Process gone but rename probe still failed — use reboot replace for Service\*.
    if not ServiceDllUnlocked then
    begin
      NeedsRestart := True;
      GNeedsRestart := True;
    end;
    Result := '';
    Exit;
  end;

  // Hard abort: service still running → do not partially overwrite files.
  Result :=
    'Could not stop the Alpha AI Tracker DLP service, so setup cannot replace ' +
    'Service\AlphaAITracker.Service.dll (Access Denied).'#13#10#13#10 +
    'Reboot this PC and run the installer again. No application files were changed.';
end;

// ────────────────────────────────────────────────
// InitializeUninstall — Alpha AI Tracker ONLY.
// Hard gate: Apps & features uninstall is refused unless
// teardown.ok exists (written after a verified removal code
// via client.exe --protected-uninstall --code …).
// No break-glass Yes — other installed software is unaffected.
// ────────────────────────────────────────────────
function InitializeUninstall: Boolean;
var
  Gate: string;
  ResultCode: Integer;
begin
  // Always release locks first — even when refusing without teardown.ok — so a
  // stuck client.exe from a hung protected-uninstall does not cause Error 32 on retry.
  Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill', '/F /IM {#WINDOWS_SERVICE_EXE_NAME}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Gate := ExpandConstant('{commonappdata}\{#APP_MUTEX}\dlp\teardown.ok');
  if not FileExists(Gate) then
  begin
    MsgBox(
      '{#MyAppName} cannot be uninstalled without an authorized removal code.'#13#10#13#10 +
      '1. On the web: Employees → Issue Removal Code'#13#10 +
      '2. On this PC run:'#13#10 +
      '   {#MyAppExeName} --protected-uninstall --code <code>'#13#10 +
      '3. Then uninstall again from Apps & features.'#13#10#13#10 +
      'This restriction applies only to {#MyAppName}.',
      mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
  // Kill + settle before SCM delete so Inno can open unins000.dat (no Error 32).
  KillRunningInstance;
  RemoveDlpService;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallOrgTlsCa;
    InstallDlpService;
    GInstallSucceeded := True;
  end;
end;

// If setup is cancelled / fails after we stopped the DLP service, Inno rolls back
// replaced files automatically. Re-register + start the (restored) service so the
// machine is not left without DLP after a failed upgrade.
procedure DeinitializeSetup();
begin
  if GInstallSucceeded then
    Exit;
  if not GServiceWasStoppedForUpgrade then
    Exit;
  if FileExists(ExpandConstant('{app}\Service\{#WINDOWS_SERVICE_EXE_NAME}')) then
    InstallDlpService;
end;

function NeedRestart(): Boolean;
begin
  // True when Service DLL stayed locked after process exit (restartreplace pending).
  Result := GNeedsRestart;
end;

procedure InitializeWizard;
begin
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;
