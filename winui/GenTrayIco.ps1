# Правильный multi-resolution PNG-в-ICO (Vista+) с солнышком MonitorTune.
# Каждый размер нарисован отдельно (т.к. простой downscale 256→16 теряет лучи).
Add-Type -AssemblyName System.Drawing
$out = "$PSScriptRoot\Assets\AppIcon.ico"

function DrawSunPng([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = "AntiAlias"
  $g.Clear([System.Drawing.Color]::Transparent)

  $cx = $size / 2.0
  $cy = $size / 2.0
  # Геометрия пересчитанная под размер
  # Геометрия с гарантированным запасом от края, чтобы лучи и
  # их обводка-edge со скруглёнными концами не вылезали из canvas.
  $rayThick = [Math]::Max(1.2, $size * 0.085)
  $edgeExtra = [Math]::Max(0.6, $size * 0.04)
  $edgeThick = $rayThick + $edgeExtra
  $safety = $edgeThick / 2.0 + 0.5
  $rayOuter = ($size / 2.0) - $safety
  $sunR = [Math]::Max(2.0, $size * 0.22)
  $rayInner = $sunR + [Math]::Max(1.0, $size * 0.06)

  $sun = [System.Drawing.Color]::FromArgb(255, 184, 0)
  $edge = [System.Drawing.Color]::FromArgb(110, 60, 0)

  $penE = New-Object System.Drawing.Pen($edge, $edgeThick); $penE.StartCap="Round"; $penE.EndCap="Round"
  $penS = New-Object System.Drawing.Pen($sun, $rayThick);    $penS.StartCap="Round"; $penS.EndCap="Round"

  # 8 лучей: сначала тёмная подложка пошире, затем янтарь
  for ($pass = 0; $pass -lt 2; $pass++) {
    $p = if ($pass -eq 0) { $penE } else { $penS }
    for ($k = 0; $k -lt 8; $k++) {
      $a = $k * [Math]::PI / 4.0
      $x1 = $cx + [Math]::Cos($a) * $rayInner
      $y1 = $cy + [Math]::Sin($a) * $rayInner
      $x2 = $cx + [Math]::Cos($a) * $rayOuter
      $y2 = $cy + [Math]::Sin($a) * $rayOuter
      $p.GetType() | Out-Null
      $g.DrawLine($p, [float]$x1, [float]$y1, [float]$x2, [float]$y2)
    }
  }

  # Ядро солнышка
  $br = New-Object System.Drawing.SolidBrush($edge)
  $g.FillEllipse($br, [float]($cx - $sunR - 0.6), [float]($cy - $sunR - 0.6), [float](2 * ($sunR + 0.6)), [float](2 * ($sunR + 0.6)))
  $br = New-Object System.Drawing.SolidBrush($sun)
  $g.FillEllipse($br, [float]($cx - $sunR), [float]($cy - $sunR), [float](2 * $sunR), [float](2 * $sunR))

  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  return $ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$pngs = New-Object 'System.Collections.Generic.List[object]'
foreach ($s in $sizes) {
  $bytes = DrawSunPng $s
  Write-Output "  $s : $($bytes.Length) байт PNG"
  $pngs.Add(@{Size=$s; Data=$bytes})
}

# Сборка ICO бинарно
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

# ICONDIR: 6 байт
$bw.Write([uint16]0)              # Reserved
$bw.Write([uint16]1)              # Type=1 (ICO)
$bw.Write([uint16]$pngs.Count)    # Count

# ICONDIRENTRY: 16 байт каждый
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
  $size = $p.Size; $data = $p.Data
  $w = if ($size -ge 256) { 0 } else { $size }
  $bw.Write([byte]$w)
  $bw.Write([byte]$w)
  $bw.Write([byte]0)
  $bw.Write([byte]0)
  $bw.Write([uint16]1)
  $bw.Write([uint16]32)
  $bw.Write([uint32]$data.Length)
  $bw.Write([uint32]$offset)
  $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p.Data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
$ms.Dispose()

Write-Output "Готово: $out ($((Get-Item $out).Length) байт), размеры: $($sizes -join ',')"
