# Диагностика MonitorTune на проблемной машине.
# Запустить в PowerShell (правая кнопка -> "Запуск от имени администратора" ЖЕЛАТЕЛЬНО, но не обязательно).
# Вывод скопировать полностью и прислать.

$out = @()
$out += "=" * 60
$out += "MonitorTune diagnostic  " + (Get-Date)
$out += "=" * 60

# 1. Windows
$out += ""
$out += "--- Windows ---"
$os = Get-CimInstance Win32_OperatingSystem
$out += "OS: $($os.Caption)  Build $($os.BuildNumber)  Version $($os.Version)"
$out += "Arch: $($os.OSArchitecture)"

# 2. GPU
$out += ""
$out += "--- GPU ---"
Get-CimInstance Win32_VideoController | ForEach-Object {
    $out += "  $($_.Name)  driver $($_.DriverVersion)  ($($_.AdapterCompatibility))"
}

# 3. Мониторы
$out += ""
$out += "--- Мониторы (WMI) ---"
try {
    Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID | ForEach-Object {
        $name = -join ($_.UserFriendlyName | Where-Object {$_ -ne 0} | ForEach-Object {[char]$_})
        $man  = -join ($_.ManufacturerName | Where-Object {$_ -ne 0} | ForEach-Object {[char]$_})
        $out += "  $man / $name / instance $($_.InstanceName)"
    }
} catch { $out += "  WMI ошибка: $($_.Exception.Message)" }

# 4. Пакет MonitorTune
$out += ""
$out += "--- Пакет MonitorTune ---"
$pkg = Get-AppxPackage MonitorTune -ErrorAction SilentlyContinue
if ($pkg) {
    $out += "PackageFullName: $($pkg.PackageFullName)"
    $out += "PackageFamilyName: $($pkg.PackageFamilyName)"
    $out += "Architecture: $($pkg.Architecture)"
    $out += "InstallLocation: $($pkg.InstallLocation)"
} else {
    $out += "MonitorTune НЕ УСТАНОВЛЕН"
}

# 5. Windows App SDK Runtime
$out += ""
$out += "--- WindowsAppRuntime ---"
Get-AppxPackage Microsoft.WindowsAppRuntime* -ErrorAction SilentlyContinue | Select-Object Name, Version, Architecture | ForEach-Object {
    $out += "  $($_.Name) $($_.Version) $($_.Architecture)"
}

# 6. Лог приложения
$out += ""
$out += "--- Лог приложения (последние 100 строк) ---"
if ($pkg) {
    $logPath = "$env:LOCALAPPDATA\Packages\$($pkg.PackageFamilyName)\LocalCache\Local\MonitorTune.log"
    if (Test-Path $logPath) {
        Get-Content $logPath -Tail 100 | ForEach-Object { $out += "  $_" }
    } else {
        $out += "  лог не найден: $logPath"
    }
}

# 7. Windows Event Log — ошибки приложения
$out += ""
$out += "--- Event Log: последние сбои MonitorTune ---"
$since = (Get-Date).AddDays(-3)
try {
    Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$since; Level=1,2,3} -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match "MonitorTune|Microsoft.UI.Xaml|WindowsAppRuntime" } |
        Select-Object -First 5 |
        ForEach-Object {
            $out += "  $($_.TimeCreated)  $($_.ProviderName)  L$($_.Level)"
            $out += "    " + ($_.Message -replace "\r?\n"," / ").Substring(0, [Math]::Min(400, $_.Message.Length))
        }
} catch { $out += "  Event Log ошибка: $($_.Exception.Message)" }

# 8. DPI
$out += ""
$out += "--- DPI ---"
try {
    $dpi = (Get-ItemProperty "HKCU:\Control Panel\Desktop\WindowMetrics" -ErrorAction SilentlyContinue).AppliedDPI
    $out += "  System AppliedDPI: $dpi"
} catch { }

$out | Out-File "$env:USERPROFILE\Desktop\MonitorTune-diagnostic.txt" -Encoding UTF8
$out | Write-Output

Write-Output ""
Write-Output "===================="
Write-Output "Файл сохранён на рабочий стол: MonitorTune-diagnostic.txt"
Write-Output "Пришли его содержимое."
Write-Output "===================="
