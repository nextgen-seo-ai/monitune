# Build-time скрипт: парсит linuxhw EDID + UEFI PNP CSV,
# выдаёт два gz-сжатых файла для embed в MSIX.
#
# Источники:
#   uefi-pnp.csv          — UEFI PNP ID Registry (общедоступно, без копирайта на данные).
#   DigitalDisplay.md     — linuxhw/EDID, CC-BY-4.0 (атрибуция в About).
#
# Выход:
#   pnp_vendors.tsv.gz    — pnp\tvendor
#   monitor_models.tsv.gz — pnp\thex_code\tmarketing_name

$src = $PSScriptRoot
$out = $PSScriptRoot

# ---- 1. PNP vendors ----
$pnpIn  = Join-Path $src "uefi-pnp.csv"
$pnpOut = Join-Path $out "pnp_vendors.tsv"
if (!(Test-Path $pnpIn)) { throw "uefi-pnp.csv не найден" }

$pnpLines = New-Object System.Collections.Generic.List[string]
Import-Csv $pnpIn -Encoding UTF8 | ForEach-Object {
    $company = $_.Company.Trim()
    $pnpId   = $_."PNP ID".Trim()
    if ($company -and $pnpId -and $pnpId.Length -eq 3) {
        $pnpLines.Add("$pnpId`t$company")
    }
}
[System.IO.File]::WriteAllLines($pnpOut, $pnpLines)
Write-Host "PNP vendors: $($pnpLines.Count) записей -> $pnpOut"

# ---- 2. Monitor models ----
$mdIn   = Join-Path $src "DigitalDisplay.md"
$tsvOut = Join-Path $out "monitor_models.tsv"
if (!(Test-Path $mdIn)) { throw "DigitalDisplay.md не найден" }

# Структура строки: | MFG | Model | Name | ... | (после первых 2 строк-заголовка)
# Берём Model (например AGN1624) и Name (marketing). PNP = первые 3 символа Model.
$rxRow = '^\|\s*([^|]+?)\s*\|\s*([A-Za-z]{3}[0-9A-Fa-f]{4})\s*\|\s*([^|]*?)\s*\|'
$dedupe = New-Object 'System.Collections.Generic.Dictionary[string,string]'
$count = 0
foreach ($line in [System.IO.File]::ReadLines($mdIn)) {
    if ($line.StartsWith("|---")) { continue }
    if (!$line.StartsWith("|")) { continue }
    $m = [regex]::Match($line, $rxRow)
    if (!$m.Success) { continue }
    $model = $m.Groups[2].Value
    $name  = $m.Groups[3].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($name)) { continue }
    $pnp  = $model.Substring(0, 3).ToUpperInvariant()
    $code = $model.Substring(3, 4).ToUpper()
    $key  = "$pnp`t$code"
    # дедуп: первое непустое имя
    if (-not $dedupe.ContainsKey($key)) {
        $dedupe[$key] = $name
        $count++
    }
}
$tsvLines = New-Object System.Collections.Generic.List[string]
foreach ($kv in $dedupe.GetEnumerator()) {
    $tsvLines.Add("$($kv.Key)`t$($kv.Value)")
}
[System.IO.File]::WriteAllLines($tsvOut, $tsvLines)
Write-Host "Monitor models: $count записей -> $tsvOut"

# ---- 3. GZip оба ----
function GzipFile($inPath, $outPath) {
    $bytes = [System.IO.File]::ReadAllBytes($inPath)
    $ms = New-Object System.IO.MemoryStream
    # leaveOpen=true чтобы Close GZipStream не закрыл MemoryStream до ToArray()
    $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionMode]::Compress, $true)
    $gz.Write($bytes, 0, $bytes.Length)
    $gz.Close()
    $ms.Flush()
    [System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
    $ratio = [math]::Round($ms.Length / $bytes.Length * 100, 1)
    Write-Host "$outPath = $([math]::Round($ms.Length/1KB,1)) KB ($ratio% от исходных $([math]::Round($bytes.Length/1KB,1)) KB)"
}

GzipFile $pnpOut "$pnpOut.gz"
GzipFile $tsvOut "$tsvOut.gz"
Remove-Item $pnpOut, $tsvOut

Write-Host "Готово."
