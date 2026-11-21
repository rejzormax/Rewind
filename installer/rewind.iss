#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be supplied by build-installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by build-installer.ps1
#endif

[Setup]
#ifdef InstallerTestMode
AppId=Rewind.Installer.Test
AppName=Rewind Setup Test
OutputBaseFilename=Rewind-Setup-Test
CreateUninstallRegKey=no
#else
AppId=Rewind
AppName=Rewind
OutputBaseFilename=Rewind-Setup
AppMutex=Local\Rewind-Setup
#endif
AppVersion={#AppVersion}
AppPublisher=rejzormax
AppPublisherURL=https://github.com/rejzormax/Rewind
AppSupportURL=https://github.com/rejzormax/Rewind/issues
AppUpdatesURL=https://github.com/rejzormax/Rewind/releases/latest
DefaultDirName={localappdata}\Programs\Rewind
DefaultGroupName=Rewind
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
SetupIconFile=..\assets\rewind.ico
UninstallDisplayIcon={app}\Rewind.exe
WizardStyle=modern
DisableWelcomePage=no
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
#ifndef InstallerTestMode
Name: "{autoprograms}\Rewind"; Filename: "{app}\Rewind.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Rewind"; Filename: "{app}\Rewind.exe"; WorkingDir: "{app}"; Tasks: desktopicon
#endif

[Run]
Filename: "{app}\Rewind.exe"; Description: "Open Rewind"; Flags: nowait postinstall skipifsilent

[Code]
var
  RecordingSetupFailed: Boolean;

function GetCustomSetupExitCode: Integer;
begin
  if RecordingSetupFailed then Result := 1 else Result := 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  DataDirectory: String;
  Prepared: Boolean;
begin
  if CurStep <> ssPostInstall then exit;
  WizardForm.StatusLabel.Caption := 'Preparing the recording files...';
#ifdef InstallerTestMode
  DataDirectory := ExpandConstant('{app}\.test-data');
#else
  DataDirectory := ExpandConstant('{localappdata}\Rewind');
#endif
  repeat
    Prepared := Exec(ExpandConstant('{app}\auto-updater.exe'), '--prepare-engine --data-dir "' + DataDirectory + '"',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if Prepared then Prepared := ResultCode = 0;
    if not Prepared then begin
      if SuppressibleMsgBox('Rewind could not download its recording files. Check your internet connection and try again.',
        mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
      begin
        RecordingSetupFailed := True;
        RaiseException('Recording setup was not completed. Run Rewind Setup again when connected to the internet.');
      end;
    end;
  until Prepared;
  WizardForm.StatusLabel.Caption := 'Rewind is ready.';
end;
