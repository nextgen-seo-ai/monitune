# Генерация иконок-солнце MonitorTune во все требуемые Store-размеры.
Add-Type -AssemblyName System.Drawing
$dst = "$PSScriptRoot\Assets"

function DrawSun([int]$size, [string]$path) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = "AntiAlias"
  $g.Clear([System.Drawing.Color]::Transparent)

  $cx = $size / 2.0
  $cy = $size / 2.0
  $rayLen = $size * 0.45
  $rayInner = $size * 0.30
  $rayThick = [Math]::Max(2, [int]($size * 0.10))
  $sunR = $size * 0.20

  $sun = [System.Drawing.Color]::FromArgb(255, 184, 0)
  $edge = [System.Drawing.Color]::FromArgb(110, 60, 0)

  # лучи
  $penEdge = New-Object System.Drawing.Pen($edge, ($rayThick + 2))
  $penEdge.StartCap = "Round"; $penEdge.EndCap = "Round"
  $penSun  = New-Object System.Drawing.Pen($sun, $rayThick)
  $penSun.StartCap = "Round"; $penSun.EndCap = "Round"

  for ($k = 0; $k -lt 8; $k++) {
    $a = $k * [Math]::PI / 4.0
    $x1 = $cx + [Math]::Cos($a) * $rayInner
    $y1 = $cy + [Math]::Sin($a) * $rayInner
    $x2 = $cx + [Math]::Cos($a) * $rayLen
    $y2 = $cy + [Math]::Sin($a) * $rayLen
    $g.DrawLine($penEdge, $x1, $y1, $x2, $y2)
  }
  for ($k = 0; $k -lt 8; $k++) {
    $a = $k * [Math]::PI / 4.0
    $x1 = $cx + [Math]::Cos($a) * $rayInner
    $y1 = $cy + [Math]::Sin($a) * $rayInner
    $x2 = $cx + [Math]::Cos($a) * $rayLen
    $y2 = $cy + [Math]::Sin($a) * $rayLen
    $g.DrawLine($penSun, $x1, $y1, $x2, $y2)
  }

  # ядро
  $br = New-Object System.Drawing.SolidBrush($edge)
  $g.FillEllipse($br, ($cx - $sunR - 1), ($cy - $sunR - 1), 2 * ($sunR + 1), 2 * ($sunR + 1))
  $br = New-Object System.Drawing.SolidBrush($sun)
  $g.FillEllipse($br, ($cx - $sunR), ($cy - $sunR), 2 * $sunR, 2 * $sunR)

  $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}

# Все размеры, которые Microsoft Store ждёт
$assets = @{
  "Square44x44Logo.scale-100.png" = 44
  "Square44x44Logo.scale-125.png" = 55
  "Square44x44Logo.scale-150.png" = 66
  "Square44x44Logo.scale-200.png" = 88
  "Square44x44Logo.scale-400.png" = 176
  "Square44x44Logo.targetsize-16.png" = 16
  "Square44x44Logo.targetsize-24.png" = 24
  "Square44x44Logo.targetsize-32.png" = 32
  "Square44x44Logo.targetsize-48.png" = 48
  "Square44x44Logo.targetsize-256.png" = 256
  "Square44x44Logo.targetsize-24_altform-unplated.png" = 24
  "Square44x44Logo.targetsize-48_altform-lightunplated.png" = 48
  "Square71x71Logo.scale-200.png" = 142
  "Square150x150Logo.scale-100.png" = 150
  "Square150x150Logo.scale-200.png" = 300
  "Square150x150Logo.scale-400.png" = 600
  "Square310x310Logo.scale-100.png" = 310
  "Wide310x150Logo.scale-100.png"  = 150
  "Wide310x150Logo.scale-200.png"  = 300
  "StoreLogo.png" = 50
  "StoreLogo.scale-100.png" = 50
  "StoreLogo.scale-200.png" = 100
  "SplashScreen.scale-200.png" = 600
  "LockScreenLogo.scale-200.png" = 48
  "AppIcon.ico" = 256
}

foreach ($k in $assets.Keys) {
  $p = Join-Path $dst $k
  if ($k -eq "AppIcon.ico") {
    # icon: возьмём 256 PNG и конвертнём
    $tmp = [System.IO.Path]::GetTempFileName() + ".png"
    DrawSun 256 $tmp
    $bmp = [System.Drawing.Bitmap]::FromFile($tmp)
    $ic = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    $fs = [System.IO.File]::Create($p)
    $ic.Save($fs); $fs.Close(); $bmp.Dispose()
    Remove-Item $tmp -ErrorAction SilentlyContinue
  } else {
    DrawSun $assets[$k] $p
  }
  Write-Output "  $k = $($assets[$k])"
}
Write-Output "Готово, файлов: $($assets.Count)"
