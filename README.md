# MoniTune

**[nextgen-seo-ai.github.io/monitune](https://nextgen-seo-ai.github.io/monitune/)** · [Скачать последнюю версию](https://github.com/nextgen-seo-ai/monitune/releases/latest/download/MonitorTune-Setup.exe)

Утилита для Windows 11 — управление яркостью и контрастностью внешних мониторов через DDC/CI, а также яркостью встроенных дисплеев ноутбуков (eDP) через WMI.

Иконка-солнышко в области уведомлений. Клик — панель с ползунками для каждого подключённого монитора. Для внешних мониторов регулировка идёт по протоколу VESA MCCS через `dxva2.dll` (реальный VCP 0x10, а не программный overlay). Для встроенных дисплеев — через WMI (`WmiMonitorBrightness` / `WmiSetBrightness`).

## Возможности

- Ползунки яркости и контраста для каждого внешнего монитора (плюс яркость для eDP)
- Синхронный режим — один регулятор на все мониторы одновременно
- Связка яркость ↔ контраст на выбранном мониторе
- Ночной режим по расписанию с плавным переходом
- Горячие клавиши (Ctrl+Alt+PgUp / PgDn — яркость, Ctrl+Alt+Shift — контраст, Ctrl+Alt+N — ночной режим)
- Автозапуск через `StartupTask` API (правильный путь для MSIX)
- Keep-awake — не даёт компьютеру или экрану заснуть, опциональная имитация активности
- Автообновления с проверкой Ed25519 подписи manifest'а и Authenticode подписи MSIX

## Установка

Скачать `MonitorTune-Setup.exe` из [последнего release](../../releases/latest) и запустить.

Инсталлятор:
1. Импортирует сертификат подписи в TrustedRoot и TrustedPeople локального компьютера
2. Ставит MSIX-пакет (self-contained — включает .NET 9 и WinAppSDK внутри)

**Требования:**
- Windows 11 или Windows 10 версии 2004 (20H1, build 19041) и выше
- x64
- Внешний монитор с поддержкой DDC/CI (проверьте что опция включена в OSD монитора) или встроенный дисплей ноутбука (eDP)

## Как это работает

Windows API `dxva2.dll` предоставляет доступ к High Level Monitor Configuration:

- `GetPhysicalMonitorsFromHMONITOR` — открывает handle к физическому монитору
- `SetVCPFeature(handle, 0x10, value)` — устанавливает VCP код яркости
- `GetVCPFeatureAndVCPFeatureReply` — читает текущее значение и максимум
- `CapabilitiesRequestAndCapabilitiesReply` — читает MCCS capabilities string

Приложение автоматически определяет:
- GPU-вендора (Nvidia / AMD / Intel / DisplayLink) — для подбора верификационной задержки
- Тип подключения (HDMI / DP / DP-over-Thunderbolt / USB-C / Internal) — для подбора throttle
- Поддержку VCP-кодов через caps string
- HDR-режим (Set проходит но яркость не меняется) — помечает как read-only

## Сборка из исходников

**Требования:**
- Visual Studio 2022+ (с Windows App SDK workload) или dotnet SDK 9
- Windows 11 или 10 21H2+

**Локальная сборка (без MSIX packaging):**
```powershell
cd winui
dotnet build -c Release -p:Platform=x64
```

**Локальная сборка MSIX (нужен свой signing cert):**
```powershell
# 1. Сгенерировать свой self-signed cert (см. release/UPDATES-FORMAT.md → "Схема генерации ключей")
# 2. Собрать:
dotnet build winui/MonitorTune.csproj -c Release -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:MSIX_SIGNING_KEY_PATH="абсолютный\путь\к\your.pfx" `
    -p:PackageCertificatePassword="ваш_пароль"
```

**Release-сборка** — идёт автоматически в GitHub Actions при push тега `vX.Y.Z`, см. `.github/workflows/release.yml`.

## Автообновления

Приложение при старте проверяет `latest.json` в последнем GitHub Release. Если есть новая версия — уведомление в трее с кнопкой Обновить. Формат manifest'а и схема подписи — `release/UPDATES-FORMAT.md`.

**Все обновления проверяются:**
1. Ed25519 подпись manifest'а (public key зашит в бинарь)
2. SHA-256 скачанного MSIX (сверка с полем в manifest'е)
3. Authenticode подпись MSIX через `WinVerifyTrust` (thumbprint издателя сверяется с константой)
4. Windows `PackageManager` ещё раз проверяет цепочку подписи при установке

Downgrade заблокирован по-умолчанию — установка версии меньше текущей требует ручного `AllowDowngrade` в settings.json.

## Лицензия

[MIT](LICENSE).

## Разработка

Основной код — `winui/`. Ключевые файлы:
- `Ddc.cs` — DDC/CI manager (Enumerate, SafeRead/SafeWrite, TryReopenHandle, thread-safety, verify)
- `App.xaml.cs` — orchestrator (async logger, crash reporter, update service wiring)
- `MainWindow.xaml*` — панель управления
- `SettingsWindow.xaml*` — настройки
- `DisplayEventsService.cs` — WM_DISPLAYCHANGE / WM_DEVICECHANGE / WM_POWERBROADCAST через message-only HWND
- `HotkeyService.cs` — RegisterHotKey через message-only HWND
- `KeepAwakeService.cs` — SetThreadExecutionState + SendInput
- `UpdateService.cs` — auto-update с Ed25519 verify + WinVerifyTrust
- `CrashReporter.cs` — UnhandledException handler
