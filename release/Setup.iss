; Inno Setup script для MonitorTune
; Собирает один setup.exe который ставит MSIX + сертификат.
; WindowsAppRuntime bundle больше НЕ нужен: MSIX self-contained (WinAppSDK + .NET внутри).

#define MyAppName "MonitorTune"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "MonitorTune"
#define MyAppURL "https://github.com/"

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
ShowLanguageDialog=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "MonitorTune_1.0.0.0_x64.msixbundle"; DestDir: "{app}"; Flags: ignoreversion
Source: "MonitorTune.cer";                    DestDir: "{app}"; Flags: ignoreversion
Source: "AppIcon.ico";                        DestDir: "{app}"; Flags: ignoreversion

[Run]
; 1. Импорт сертификата в Trusted Root и TrustedPeople
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Import-Certificate -FilePath '{app}\MonitorTune.cer' -CertStoreLocation Cert:\LocalMachine\Root | Out-Null"""; \
    StatusMsg: "Импорт сертификата подписи..."; \
    Flags: waituntilterminated runhidden
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Import-Certificate -FilePath '{app}\MonitorTune.cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"""; \
    Flags: waituntilterminated runhidden

; 2. Установить MSIX
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Add-AppxPackage -Path '{app}\MonitorTune_1.0.0.0_x64.msixbundle'"""; \
    StatusMsg: "Установка приложения MoniTune..."; \
    Flags: waituntilterminated runhidden

; 3. Запустить приложение по окончании (опционально, чекбокс)
Filename: "explorer.exe"; \
    Parameters: "shell:AppsFolder\MonitorTune_xz878f8xj2bm6!App"; \
    Description: "Запустить {#MyAppName}"; \
    Flags: postinstall nowait skipifsilent shellexec

[UninstallRun]
; При удалении убрать MSIX (сертификат и runtime оставляем — они могут быть нужны другим приложениям)
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage MonitorTune | Remove-AppxPackage"""; \
    Flags: waituntilterminated runhidden
