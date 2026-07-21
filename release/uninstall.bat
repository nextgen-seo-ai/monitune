@echo off
chcp 65001 >nul
title Удаление MoniTune
echo Удаление MoniTune...
powershell -ExecutionPolicy Bypass -Command ^
    "Get-AppxPackage MonitorTune | Remove-AppxPackage; Write-Host 'Готово.'"
pause
