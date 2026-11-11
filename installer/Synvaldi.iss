#define Version "0.1.0"
[Setup]
AppId={{843EDF4A-0EF8-409A-8B4D-BDB485E0D9DC}
AppName=Synvaldi
AppVersion={#Version}
AppPublisher=Synvaldi
AppPublisherURL=https://github.com/korsun009/Synvaldi
DefaultDirName={localappdata}\Programs\Synvaldi
PrivilegesRequired=lowest
DefaultGroupName=Synvaldi
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\app.ico
SetupIconFile=..\assets\app.ico
WizardStyle=modern
WizardImageFile=wizard.bmp
WizardSmallImageFile=wizard-small.bmp
WizardSizePercent=110
Compression=lzma2
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=Synvaldi-{#Version}
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#Version}.0
ArchitecturesAllowed=x64compatible

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\desktop\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\publish\host\Synvaldi.NativeHost.*"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\updater\Synvaldi.Update.*"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\browser\extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs
Source: "..\assets\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Synvaldi"; Filename: "{app}\Synvaldi.exe"; Parameters: "--tray"; IconFilename: "{app}\app.ico"

[Registry]
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.synvaldi.bridge"; ValueType: string; ValueName: ""; ValueData: "{app}\bridge-manifest.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Chromium\NativeMessagingHosts\com.synvaldi.bridge"; ValueType: string; ValueName: ""; ValueData: "{app}\bridge-manifest.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Vivaldi\NativeMessagingHosts\com.synvaldi.bridge"; ValueType: string; ValueName: ""; ValueData: "{app}\bridge-manifest.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Synvaldi"; ValueData: """{app}\Synvaldi.exe"" --tray"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Synvaldi.exe"; Parameters: "--setup"; Flags: nowait skipifsilent

[UninstallRun]
Filename: "{app}\Synvaldi.exe"; Parameters: "--quit"; RunOnceId: "SynvaldiQuit"; Flags: runhidden waituntilterminated skipifdoesntexist

[UninstallDelete]
Type: files; Name: "{app}\bridge-manifest.json"

[Code]
var
  Connection: TInputFileWizardPage;

procedure InitializeWizard();
var
  Supplied: String;
begin
  Connection := CreateInputFilePage(wpSelectDir, 'Подключение к серверу',
    'Профиль подключения Synvaldi', 'Если владелец сервера передал файл подключения, выбери его здесь. Адрес сервера также можно указать в приложении.');
  Connection.Add('Файл подключения (необязательно):', 'Synvaldi (*.json)|*.json', '.json');
  Supplied := ExpandConstant('{src}\connection.json');
  if FileExists(Supplied) then Connection.Values[0] := Supplied;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = Connection.ID) and (Connection.Values[0] <> '') and not FileExists(Connection.Values[0]) then begin
    MsgBox('Не удалось найти файл подключения.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Manifest: String;
  HostPath: String;
begin
  if CurStep = ssPostInstall then begin
    if (not WizardSilent) and (Connection.Values[0] <> '') then begin
      ForceDirectories(ExpandConstant('{localappdata}\Synvaldi'));
      if not CopyFile(Connection.Values[0], ExpandConstant('{localappdata}\Synvaldi\connection.json'), False) then
        RaiseException('Не удалось сохранить профиль подключения.');
    end;
    HostPath := ExpandConstant('{app}\Synvaldi.NativeHost.exe');
    StringChangeEx(HostPath, '\', '\\', True);
    Manifest := '{"name":"com.synvaldi.bridge","description":"Synvaldi","path":"' + HostPath + '","type":"stdio","allowed_origins":["chrome-extension://jcfipkamaklpdghopicgfllhnigfjcio/"]}';
    if not SaveStringToFile(ExpandConstant('{app}\bridge-manifest.json'), UTF8Encode(Manifest), False) then
      RaiseException('Cannot write native messaging manifest');
  end;
end;

