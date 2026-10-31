#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "1.0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\windows-native\artifacts\publish\WakePcBridge.exe"
#endif

#define AppName "Wake PC Companion"
#define AppExeName "WakePcBridge.exe"
#define AppUrl "https://github.com/patrickshao/wake-pc"

[Setup]
AppId={{EE345E29-5C07-44F9-A41B-5826C96C6356}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Wake PC contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (c) Wake PC contributors
DefaultDirName={autopf}\Wake PC Sleep Bridge
DefaultGroupName=Wake PC
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=..\windows-native\artifacts
OutputBaseFilename=wake-pc-sleep-bridge-v{#AppVersion}-setup
SetupIconFile=..\windows-native\WakePcBridge\Assets\WakePcBridge.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=force
RestartApplications=no
RestartIfNeededByRun=no
AllowNoIcons=no
UsePreviousAppDir=yes
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription={#AppName} installer
VersionInfoCompany=Wake PC contributors
#ifdef UseSignTool
SignTool=wakepc
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#AppExeName}"; \
  Flags: ignoreversion; AfterInstall: ConfigureSystem
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; \
  Flags: ignoreversion

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "WakePcSleepBridge"; \
  ValueData: """{app}\{#AppExeName}"" supervise"; \
  Flags: uninsdeletevalue

[Icons]
Name: "{commonprograms}\Wake PC Companion"; \
  Filename: "{app}\{#AppExeName}"; \
  Parameters: "start"; \
  WorkingDir: "{app}"
Name: "{commonprograms}\Wake PC Setup & Status"; \
  Filename: "{app}\{#AppExeName}"; \
  Parameters: "setup-guide"; \
  WorkingDir: "{app}"

[Messages]
FinishedLabel=Wake PC Companion is installed.%n%nUse Setup & status from the tray icon when you are ready.

[Run]
Filename: "{app}\{#AppExeName}"; \
  Parameters: "start"; \
  Description: "Start Wake PC Companion"; \
  Flags: postinstall nowait runasoriginaluser skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExeName}"; \
  Parameters: "unconfigure-system"; \
  RunOnceId: "UnconfigureWakePcSleepBridge"; \
  Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
var
  TrustedNetworkPage: TOutputMsgWizardPage;
  BridgeStoppedForSetup: Boolean;
  UpgradingFromSharedData: Boolean;

function InitializeSetup: Boolean;
begin
  // Version 1.0.0 stored data here. Only its existence is checked; nothing is read from it.
  UpgradingFromSharedData := DirExists(ExpandConstant('{commonappdata}\WakePcSleepBridge'));
  Result := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and UpgradingFromSharedData then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Wake PC Companion now keeps its data in your user profile, so Sleep PC needs ' +
      'pairing once more. Choose Pair for Sleep in Setup & status, then enter the code in Wake PC.';
end;

function TrustedNetworkAcceptedByParameter: Boolean;
begin
  Result := CompareText(
    ExpandConstant('{param:TRUSTEDNETWORK|0}'),
    '1') = 0;
end;

procedure InitializeWizard;
begin
  TrustedNetworkPage := CreateOutputMsgPage(
    wpSelectDir,
    'Wake PC Companion',
    'Optional Sleep PC and setup tools',
    'Wake works without this companion. Install it if you want Sleep PC ' +
      'and a simple setup and status view. Sleep PC uses a one-time pairing ' +
      'code and is limited to your local network. Continue only on a network ' +
      'you trust.');
end;

procedure StopInstalledBridgeForSetup;
var
  ResultCode: Integer;
begin
  if BridgeStoppedForSetup then
    Exit;
  BridgeStoppedForSetup := True;

  if not FileExists(ExpandConstant('{app}\{#AppExeName}')) then
    Exit;

  Log('Stopping the existing Wake PC supervisor and runtime before update.');
  if not Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/F /T /IM {#AppExeName}',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Log('The existing Wake PC processes could not be stopped before update.');
  end;
  Sleep(500);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = wpReady then
    StopInstalledBridgeForSetup;
  Result := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result :=
    (PageID = TrustedNetworkPage.ID) and
    (WizardSilent or TrustedNetworkAcceptedByParameter);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if WizardSilent and not TrustedNetworkAcceptedByParameter then
  begin
    Result :=
      'Silent installation requires /TRUSTEDNETWORK=1 to confirm the local ' +
      'network firewall scope.';
    Exit;
  end;
  StopInstalledBridgeForSetup;
end;

procedure ConfigureSystem;
var
  ResultCode: Integer;
  Executable: String;
  Parameters: String;
begin
  Executable := ExpandConstant('{app}\{#AppExeName}');
  Parameters := 'configure-system';
  Log('Configuring HTTP.sys and Windows Firewall.');
  if not Exec(
    Executable,
    Parameters,
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    RaiseException(
      'Wake PC system configuration could not start: ' +
        SysErrorMessage(DLLGetLastError));
  end;
  if ResultCode <> 0 then
  begin
    RaiseException(Format('Wake PC system configuration failed with exit code %d. See %s.', [ResultCode, ExpandConstant('{app}\WakePcBridge.error.txt')]));
  end;
end;
