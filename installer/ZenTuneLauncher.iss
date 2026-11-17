; Lager ZenLauncher-Setup-<versjon>.exe. Bygges av build.bat (valg 2) etter at dist\ZenTuneLauncher.msix er signert.
#ifndef AppVer
  #define AppVer "0.2"
#endif
[Setup]
AppId={{B7C1D6A2-5E0F-4B7A-9C55-2F6D4A1E7B11}
AppName=ZenLauncher
AppVersion=0.2.0.0
AppPublisher=ZenForge
DefaultDirName={autopf}\ZenLauncher
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\dist
OutputBaseFilename=ZenLauncher-Setup-{#AppVer}
Compression=lzma2
SolidCompression=yes
UninstallDisplayName=ZenLauncher
WizardStyle=modern

[Files]
Source: "..\dist\ZenTuneLauncher.msix"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\zenforge.cer"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{sys}\certutil.exe"; Parameters: "-addstore -f TrustedPeople ""{app}\zenforge.cer"""; Flags: runhidden waituntilterminated; StatusMsg: "Stoler paa ZenForge-sertifikatet..."

[Code]
function DevModeOn: Boolean;
var
  v: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock',
    'AllowDevelopmentWithoutDevLicense', v) and (v = 1);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not DevModeOn then
    MsgBox('Utviklermodus er av i Windows. ZenLauncher bruker en spesiell hjem-app-tillatelse som krever det.' + #13#10 +
      'Slaa den paa under Innstillinger - System - For utviklere, og kjoer setup paa nytt hvis installasjonen feiler.',
      mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  rc: Integer;
  ps, logFile: String;
  msg: AnsiString;
begin
  if CurStep = ssPostInstall then
  begin
    logFile := GetEnv('TEMP') + '\zt-install.txt';
    DeleteFile(logFile);
    // Stopper appen, oppdaterer på plass, og fjerner den gamle versjonen automatisk hvis oppdateringen ikke går (f.eks. et utviklingsbygg registrert fra mappe).
    ps := '-NoProfile -ExecutionPolicy Bypass -Command "$p = ''' + ExpandConstant('{app}\ZenTuneLauncher.msix') + '''; try { ' +
      'Get-Process -Name ZenTuneLauncher -ErrorAction SilentlyContinue | Stop-Process -Force; ' +
      'try { Add-AppxPackage -Path $p -ForceUpdateFromAnyVersion -ErrorAction Stop } ' +
      'catch { Get-AppxPackage -Name ZenForge.ZenTuneLauncher | Remove-AppxPackage -ErrorAction Stop; Add-AppxPackage -Path $p -ForceUpdateFromAnyVersion -ErrorAction Stop } ' +
      '} catch { $_.Exception.Message | Out-File -FilePath ($env:TEMP + ''\zt-install.txt'') -Encoding utf8; exit 1 }"';
    if (not ExecAsOriginalUser('powershell.exe', ps, '', SW_HIDE, ewWaitUntilTerminated, rc)) or (rc <> 0) then
    begin
      msg := '';
      LoadStringFromFile(logFile, msg);
      MsgBox('Appen ble ikke installert.' + #13#10#13#10 + String(msg), mbError, MB_OK);
    end;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := MsgBox('VIKTIG: Sett "Choose home app" tilbake til Xbox eller None under Innstillinger - Spill - Xbox mode FOER du avinstallerer.' + #13#10 +
    'Ellers kan Oppgavevisning slutte aa virke paa noen Windows-versjoner.' + #13#10#13#10 +
    'Har du byttet hjem-app tilbake?', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  rc: Integer;
begin
  if CurUninstallStep = usUninstall then
    ExecAsOriginalUser('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -Command "Get-AppxPackage -Name ZenForge.ZenTuneLauncher | Remove-AppxPackage"',
      '', SW_HIDE, ewWaitUntilTerminated, rc);
end;
