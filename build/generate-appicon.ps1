# Generates the FluentGpu app icon (multi-res .ico) + MSIX logo PNGs from the source artwork
# (build/appicon-source.png — the F-monogram). High-quality bicubic downscale per target size.
# Run:  powershell -ExecutionPolicy Bypass -File build/generate-appicon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$source = Join-Path $PSScriptRoot 'appicon-source.png'
$outDir = Join-Path $root 'src\FluentGpu.WindowsApp\assets\AppIcon'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (-not (Test-Path $source)) { throw "missing source artwork: $source" }

function New-RoundPath([double]$x,[double]$y,[double]$w,[double]$h,[double]$r){
  $d=$r*2; $p=New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x,$y,$d,$d,180,90); $p.AddArc($x+$w-$d,$y,$d,$d,270,90)
  $p.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90); $p.AddArc($x,$y+$h-$d,$d,$d,90,90)
  $p.CloseFigure(); return $p
}

$srcImg = [System.Drawing.Image]::FromFile($source)
# normalize to a square center-crop (source is already square, but be safe)
$side = [Math]::Min($srcImg.Width, $srcImg.Height)
$srcSquare = New-Object System.Drawing.Bitmap($side,$side,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$sg = [System.Drawing.Graphics]::FromImage($srcSquare)
$sg.DrawImage($srcImg, (New-Object System.Drawing.Rectangle(0,0,$side,$side)),
              [int](($srcImg.Width-$side)/2),[int](($srcImg.Height-$side)/2),$side,$side,[System.Drawing.GraphicsUnit]::Pixel)
$sg.Dispose(); $srcImg.Dispose()

# The artwork is a full-bleed rounded square with OPAQUE BLACK corners. Clip to a rounded-rect mask at the source
# resolution (radius ~16.5% — just over the measured 15.5% so no black sliver survives) so the corners become
# transparent, matching the icon's shape. Downscaling this master keeps the corner anti-aliasing crisp.
$srcMaskRad = $side * 0.165
$srcMasked = New-Object System.Drawing.Bitmap($side,$side,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$mg = [System.Drawing.Graphics]::FromImage($srcMasked)
$mg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$mg.Clear([System.Drawing.Color]::Transparent)
$maskPath = New-RoundPath 0 0 $side $side $srcMaskRad
$mg.SetClip($maskPath)
$mg.DrawImage($srcSquare, 0, 0, $side, $side)
$mg.Dispose(); $maskPath.Dispose(); $srcSquare.Dispose()
$srcSquare = $srcMasked   # downstream resizes the masked (transparent-corner) master

function New-IconBitmap([int]$size){
  $bmp = New-Object System.Drawing.Bitmap($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)
  $attr = New-Object System.Drawing.Imaging.ImageAttributes
  $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)   # kills edge halo on downscale
  $dest = New-Object System.Drawing.Rectangle(0,0,$size,$size)
  $g.DrawImage($srcSquare,$dest,0,0,$side,$side,[System.Drawing.GraphicsUnit]::Pixel,$attr)
  $g.Dispose(); $attr.Dispose()
  return $bmp
}
function Save-Png($bmp,$path){ $bmp.Save($path,[System.Drawing.Imaging.ImageFormat]::Png) }

# --- MSIX logo PNGs (sizes per the appmanifest VisualElements + scaled assets) ---
$logos = [ordered]@{
  'Square44x44Logo'=44; 'Square71x71Logo'=71; 'Square150x150Logo'=150
  'Square310x310Logo'=310; 'StoreLogo'=50; 'Square30x30Logo'=30
}
foreach($name in $logos.Keys){ $b=New-IconBitmap $logos[$name]; Save-Png $b (Join-Path $outDir "$name.png"); $b.Dispose() }
foreach($pair in @(@('Square44x44Logo',88),@('Square150x150Logo',300),@('StoreLogo',100))){
  $b=New-IconBitmap $pair[1]; Save-Png $b (Join-Path $outDir ("{0}.scale-200.png" -f $pair[0])); $b.Dispose()
}

# Wide310x150: the square mark centered on a dark-navy field matching the artwork
$wide=New-Object System.Drawing.Bitmap(310,150,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$wg=[System.Drawing.Graphics]::FromImage($wide)
$wg.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$wg.SmoothingMode=[System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$wg.Clear([System.Drawing.Color]::FromArgb(255,13,17,42))   # #0D112A navy
$mark=New-IconBitmap 130
$wg.DrawImage($mark,90,10,130,130)
$wg.Dispose(); $wide.Save((Join-Path $outDir 'Wide310x150Logo.png'),[System.Drawing.Imaging.ImageFormat]::Png); $mark.Dispose(); $wide.Dispose()

# --- multi-resolution .ico (PNG-compressed entries; Vista+ supports PNG in ICO) ---
$sizes = 16,24,32,48,64,128,256
$png=@{}
foreach($s in $sizes){ $b=New-IconBitmap $s; $ms=New-Object System.IO.MemoryStream; $b.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png); $png[$s]=$ms.ToArray(); $ms.Dispose(); $b.Dispose() }
$icoPath=Join-Path $outDir 'appicon.ico'
$fs=[System.IO.File]::Create($icoPath); $bw=New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset=6+16*$sizes.Count
foreach($s in $sizes){ $len=$png[$s].Length; $wh=if($s -ge 256){0}else{$s}
  $bw.Write([byte]$wh); $bw.Write([byte]$wh); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset+=$len }
foreach($s in $sizes){ $bw.Write($png[$s]) }
$bw.Flush(); $fs.Close()
$srcSquare.Dispose()

Write-Output ("wrote {0} ({1} bytes) + {2} logos + wide/scale variants" -f $icoPath,(Get-Item $icoPath).Length,$logos.Count)
