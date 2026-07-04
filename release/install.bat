@echo off
chcp 65001 >nul
title Установка MonitorTune
echo ============================================================
echo MonitorTune - установка
echo ============================================================
echo.

REM Проверка прав администратора (для импорта сертификата)
net session >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo Перезапуск с правами администратора...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"

echo [1/3] Установка Windows App SDK Runtime...
if exist "WindowsAppRuntime-x64.exe" (
    start /wait "" "WindowsAppRuntime-x64.exe" --quiet
    echo       Готово.
) else (
    echo       Файл WindowsAppRuntime-x64.exe не найден, пропускаем.
)
echo.

echo [2/3] Импорт сертификата в доверенные...
powershell -ExecutionPolicy Bypass -Command ^
    "Import-Certificate -FilePath '%~dp0MonitorTune.cer' -CertStoreLocation Cert:\LocalMachine\Root | Out-Null; ^
     Import-Certificate -FilePath '%~dp0MonitorTune.cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
echo       Готово.
echo.

echo [3/3] Установка MonitorTune...
powershell -ExecutionPolicy Bypass -Command ^
    "Add-AppxPackage -Path '%~dp0MonitorTune_1.0.0.0_x64.msixbundle'"
if %ERRORLEVEL% EQU 0 (
    echo       Готово.
    echo.
    echo ============================================================
    echo Установка завершена.
    echo Иконка-солнышко появится в области уведомлений Windows.
    echo Запустить можно из меню Пуск ^>^> MonitorTune.
    echo ============================================================
) else (
    echo       Ошибка установки. Проверьте журнал выше.
)
echo.
pause
