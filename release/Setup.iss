; Inno Setup script для MonitorTune
; Собирает один setup.exe который ставит MSIX + сертификат.
; WindowsAppRuntime bundle больше НЕ нужен: MSIX self-contained (WinAppSDK + .NET внутри).

#define MyAppName "MonitorTune"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "MonitorTune"
#define MyAppURL "https://nextgen-seo-ai.github.io/monitune/"

[Setup]
AppId={{F1C8E2D5-7B3E-4F90-9C1A-DD3F6F8A2E11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=MonitorTune-Setup
SetupIconFile=AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\AppIcon.ico
WizardImageFile=
DisableWelcomePage=no
; auto — язык берётся из настроек Windows, диалог показывается только если
; ни один из наших языков не совпал с системным
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.ImportingCert=Importing signing certificate...
russian.ImportingCert=Импорт сертификата подписи...
english.InstallingApp=Installing MoniTune...
russian.InstallingApp=Установка приложения MoniTune...
; Имя бренда пишем явно: MyAppName — это MonitorTune, оно задаёт пути установки
; и запись в списке программ, менять его нельзя без переезда папки у всех пользователей.
english.LaunchApp=Launch MoniTune
russian.LaunchApp=Запустить MoniTune

[InstallDelete]
; Убрать orphan WindowsAppRuntime-x64.exe (~100 MB) при апгрейде с v1.0.11-v1.0.13 →
; в v1.0.14+ этот bundle больше не нужен (MSIX self-contained), но старый инсталлер
; оставил его в Program Files\MonitorTune. Экономим ~100 MB диска юзера.
Type: files; Name: "{app}\WindowsAppRuntime-x64.exe"

[Files]
Source: "MonitorTune_1.0.0.0_x64.msixbundle"; DestDir: "{app}"; Flags: ignoreversion
Source: "MonitorTune.cer";                    DestDir: "{app}"; Flags: ignoreversion
Source: "AppIcon.ico";                        DestDir: "{app}"; Flags: ignoreversion

[Run]
; 1. Импорт сертификата в Trusted Root и TrustedPeople
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Import-Certificate -FilePath '{app}\MonitorTune.cer' -CertStoreLocation Cert:\LocalMachine\Root | Out-Null"""; \
    StatusMsg: "{cm:ImportingCert}"; \
    Flags: waituntilterminated runhidden
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Import-Certificate -FilePath '{app}\MonitorTune.cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"""; \
    Flags: waituntilterminated runhidden

; 2. Установить MSIX
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Add-AppxPackage -Path '{app}\MonitorTune_1.0.0.0_x64.msixbundle'"""; \
    StatusMsg: "{cm:InstallingApp}"; \
    Flags: waituntilterminated runhidden

; 3. Запустить приложение по окончании (опционально, чекбокс).
; AUMID собираем из PackageFamilyName на месте, а не строкой в скрипте: хвост
; PackageFamilyName — это хеш от Publisher, и при смене сертификата он меняется.
; Зашитый хеш от прежнего сертификата молча ломал этот чекбокс — установка
; проходила, а приложение не запускалось.
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""$pfn = (Get-AppxPackage MonitorTune).PackageFamilyName; if ($pfn) { Start-Process ('shell:AppsFolder\' + $pfn + '!App') }"""; \
    Description: "{cm:LaunchApp}"; \
    Flags: postinstall nowait skipifsilent

[UninstallRun]
; При удалении убрать MSIX (сертификат и runtime оставляем — они могут быть нужны другим приложениям)
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage MonitorTune | Remove-AppxPackage"""; \
    Flags: waituntilterminated runhidden
