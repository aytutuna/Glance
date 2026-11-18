#ifndef AppVersion
  #define AppVersion "1.3.0"
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
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

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
arabic.StartWithWindows=التشغيل عند بدء Windows
arabic.LaunchGlance=تشغيل Glance
arabic.DeleteSettings=هل تريد حذف إعدادات Glance والمفاتيح المحفوظة والإجابات الأخيرة أيضًا؟
german.StartWithWindows=Mit Windows starten
german.LaunchGlance=Glance starten
german.DeleteSettings=Auch Ihre Glance-Einstellungen, gespeicherten Schlüssel und letzten Antworten löschen?
japanese.StartWithWindows=Windowsの起動時に開始
japanese.LaunchGlance=Glanceを起動
japanese.DeleteSettings=Glanceの設定、保存したキー、最近の回答も削除しますか？
russian.StartWithWindows=Запускать вместе с Windows
russian.LaunchGlance=Запустить Glance
russian.DeleteSettings=Удалить также настройки Glance, сохранённые ключи и последние ответы?
spanish.StartWithWindows=Iniciar con Windows
spanish.LaunchGlance=Iniciar Glance
spanish.DeleteSettings=¿Eliminar también la configuración de Glance, las claves guardadas y las respuestas recientes?
french.StartWithWindows=Lancer au démarrage de Windows
french.LaunchGlance=Lancer Glance
french.DeleteSettings=Supprimer aussi vos paramètres Glance, vos clés enregistrées et vos réponses récentes ?
brazilianportuguese.StartWithWindows=Iniciar com o Windows
brazilianportuguese.LaunchGlance=Iniciar o Glance
brazilianportuguese.DeleteSettings=Excluir também as configurações do Glance, as chaves salvas e as respostas recentes?
italian.StartWithWindows=Avvia con Windows
italian.LaunchGlance=Avvia Glance
italian.DeleteSettings=Eliminare anche le impostazioni di Glance, le chiavi salvate e le risposte recenti?
korean.StartWithWindows=Windows 시작 시 실행
korean.LaunchGlance=Glance 시작
korean.DeleteSettings=Glance 설정, 저장된 키, 최근 답변도 삭제하시겠습니까?
polish.StartWithWindows=Uruchamiaj z systemem Windows
polish.LaunchGlance=Uruchom Glance
polish.DeleteSettings=Czy usunąć także ustawienia Glance, zapisane klucze i ostatnie odpowiedzi?
dutch.StartWithWindows=Starten met Windows
dutch.LaunchGlance=Glance starten
dutch.DeleteSettings=Ook uw Glance-instellingen, opgeslagen sleutels en recente antwoorden verwijderen?

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // the app can switch this value on by itself from its menu, so it is removed whether or not the task was ticked
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Glance');
  if CurUninstallStep = usPostUninstall then
    if UninstallSilent or (MsgBox(CustomMessage('DeleteSettings'), mbConfirmation, MB_YESNO) = IDYES) then
      DelTree(ExpandConstant('{userappdata}\Glance'), True, True, True);
end;
