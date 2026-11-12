#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6F0C3A52-8B1E-4D7A-9C41-2E5B7A0D93F1}
AppName=Glance
AppVersion={#AppVersion}
AppPublisher=Aytu Tuna
AppPublisherURL=https://github.com/aytutuna/Glance
DefaultDirName={localappdata}\Programs\Glance
DefaultGroupName=Glance
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\Glance.ico
UninstallDisplayIcon={app}\Glance.exe
OutputBaseFilename=Glance-{#AppVersion}-setup
OutputDir=..\publish
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=Local\Glance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "startup"; Description: "{cm:StartWithWindows}"; Flags: unchecked

[Files]
Source: "..\publish\portable\Glance.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Glance"; Filename: "{app}\Glance.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Glance"; ValueData: """{app}\Glance.exe"""; Tasks: startup

[Run]
Filename: "{app}\Glance.exe"; Description: "{cm:LaunchGlance}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
english.StartWithWindows=Start with Windows
english.LaunchGlance=Start Glance
turkish.StartWithWindows=Windows ile başlat
turkish.LaunchGlance=Glance'ı başlat
english.DeleteSettings=Also delete your Glance settings, saved keys and recent answers?
turkish.DeleteSettings=Glance ayarlarınız, kayıtlı anahtarlarınız ve son yanıtlarınız da silinsin mi?

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // the app can switch this value on by itself from its menu, so it is removed whether or not the task was ticked
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Glance');
  if CurUninstallStep = usPostUninstall then
    if UninstallSilent or (MsgBox(CustomMessage('DeleteSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{userappdata}\Glance'), True, True, True);
end;
