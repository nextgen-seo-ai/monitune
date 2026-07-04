@echo off
chcp 65001 >nul
title Удаление MonitorTune
echo Удаление MonitorTune...
powershell -ExecutionPolicy Bypass -Command ^
    "Get-AppxPackage MonitorTune | Remove-AppxPackage; Write-Host 'Готово.'"
pause
