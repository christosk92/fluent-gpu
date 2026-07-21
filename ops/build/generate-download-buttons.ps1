# Generates app-store-style "Download installer" button PNGs (x64 + arm64, light + dark) for the README,
# matching the WaveeMusic style. Run:  powershell -ExecutionPolicy Bypass -File ops/build/generate-download-buttons.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outDir = Join-Path $root 'docs\media\download'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$S = 2                        # 2x render for retina; displayed at half height
$W = 300 * $S; $H = 64 * $S

# the FluentGpu app logo (F monogram, transparent corners) goes IN the button
$logoPath = Join-Path $root 'src\FluentGpu.WindowsApp\assets\AppIcon\Square310x310Logo.png'
if (-not (Test-Path $logoPath)) { throw "missing $logoPath - run ops/build/generate-appicon.ps1 first" }
$logo = [System.Drawing.Image]::FromFile($logoPath)

function New-RoundPath([double]$x,[double]$y,[double]$w,[double]$h,[double]$r){
  $d=$r*2; $p=New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x,$y,$d,$d,180,90); $p.AddArc($x+$w-$d,$y,$d,$d,270,90)
  $p.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90); $p.AddArc($x,$y+$h-$d,$d,$d,90,90); $p.CloseFigure(); return $p
}
function C([int]$r,[int]$g,[int]$b,[int]$a=255){ [System.Drawing.Color]::FromArgb($a,$r,$g,$b) }

function New-Button([string]$arch,[bool]$dark){
  $bmp=New-Object System.Drawing.Bitmap($W,$H,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g=[System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode=[System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.TextRenderingHint=[System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
  $g.Clear([System.Drawing.Color]::Transparent)

  if($dark){ $bg=C 23 28 52; $border=C 52 60 96; $fg=C 255 255 255; $sub=C 174 180 200 }
  else     { $bg=C 255 255 255; $border=C 214 219 230; $fg=C 17 22 43; $sub=C 96 104 128 }
  $accent1=C 59 130 246; $accent2=C 24 194 212

  $pad=1*$S
  $rect=New-Object System.Drawing.Rectangle($pad,$pad,($W-2*$pad),($H-2*$pad))
  $path=New-RoundPath $pad $pad ($W-2*$pad) ($H-2*$pad) (14*$S)
  $g.FillPath((New-Object System.Drawing.SolidBrush $bg),$path)
  $pen=New-Object System.Drawing.Pen($border, (1.5*$S)); $g.DrawPath($pen,$path)

  # the FluentGpu app logo (F monogram), not a generic download glyph
  $isz=40*$S; $ix=15*$S; $iy=($H-$isz)/2
  $g.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode=[System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.DrawImage($logo, [int]$ix, [int]$iy, [int]$isz, [int]$isz)

  $tx=66*$S
  $small=New-Object System.Drawing.Font('Segoe UI',(8.5*$S),[System.Drawing.FontStyle]::Regular,[System.Drawing.GraphicsUnit]::Pixel)
  $big  =New-Object System.Drawing.Font('Segoe UI Semibold',(15*$S),[System.Drawing.FontStyle]::Bold,[System.Drawing.GraphicsUnit]::Pixel)
  $g.DrawString('GET THE INSTALLER',$small,(New-Object System.Drawing.SolidBrush $sub),$tx,(15*$S))
  $g.DrawString(("Windows " + $arch),$big,(New-Object System.Drawing.SolidBrush $fg),($tx-2*$S),(28*$S))

  $g.Dispose()
  return $bmp
}

foreach($arch in 'x64','ARM64'){
  $a = $arch.ToLower()
  foreach($pair in @(@($true,'dark'),@($false,'light'))){
    $b=New-Button $arch $pair[0]
    $b.Save((Join-Path $outDir ("download-$a-$($pair[1]).png")),[System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
  }
}
Write-Output "wrote download buttons -> $outDir"
Get-ChildItem $outDir -Filter *.png | Select-Object Name,Length | Format-Table -AutoSize | Out-String | Write-Output
