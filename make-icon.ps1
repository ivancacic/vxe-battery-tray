# Generates app.ico (a green battery glyph) for embedding into the exe.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

$bmp = New-Object System.Drawing.Bitmap 32,32
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$wpen  = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 2
$green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(46,160,67))
$bolt  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,214,0))

# battery body outline + terminal
$g.DrawRectangle($wpen, 3, 9, 21, 14)
$g.FillRectangle($white, 25, 13, 4, 6)
# green charge fill
$g.FillRectangle($green, 6, 12, 15, 8)
# small bolt
$pts = @(
  (New-Object System.Drawing.Point 16,10),(New-Object System.Drawing.Point 11,18),
  (New-Object System.Drawing.Point 14,18),(New-Object System.Drawing.Point 10,25),
  (New-Object System.Drawing.Point 19,16),(New-Object System.Drawing.Point 15,16),
  (New-Object System.Drawing.Point 18,10)
)
$g.FillPolygon($bolt, [System.Drawing.Point[]]$pts)
$g.Dispose()

$hicon = $bmp.GetHicon()
$icon  = [System.Drawing.Icon]::FromHandle($hicon)
$path  = Join-Path $dir 'app.ico'
$fs = [System.IO.File]::Create($path)
$icon.Save($fs)
$fs.Close()
$bmp.Dispose()
Write-Host "Wrote $path"
