<#
.SYNOPSIS
  Build a signed MSIX for Wavee (NativeAOT packaged Win32 full-trust).

.DESCRIPTION
  Pipeline:  dotnet publish -> stage layout + tile logos -> makepri -> makeappx pack -> signtool sign -> .msix
  Same-OS NativeAOT cross-arch (ARM64 host -> win-x64) is supported if the VS C++ x64/x86 build tools are installed.

.EXAMPLE
  powershell -File ops\build\pack-wavee-msix.ps1 -Arch x64
  powershell -File ops\build\pack-wavee-msix.ps1 -Arch x64 -NoAot   # self-contained JIT if AOT cannot target this arch
#>
#requires -Version 5.1
[CmdletBinding()]
param(
  [ValidateSet('arm64','x64')]
  [string]$Arch = $(
    $a = $env:PROCESSOR_ARCHITEW6432
    if (-not $a) { $a = $env:PROCESSOR_ARCHITECTURE }
    if ("$a" -match 'ARM64') { 'arm64' } else { 'x64' }),
  [string]$Version = '0.1.1.0',
  [string]$Configuration = 'Release',
  [string]$Publisher = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL',
  [string]$OutputDir = 'artifacts',
  [switch]$NoAot,
  [switch]$NoSign,
  [switch]$Install,
  [switch]$TrustedSigning,
  [string]$Metadata,
  [string]$Subscription = 'Azure subscription 1'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version must be 4-part numeric (e.g. 0.1.1.0); got '$Version'." }
if ($TrustedSigning -and -not $PSBoundParameters.ContainsKey('Publisher')) {
  $Publisher = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL'
}

$buildDir = $PSScriptRoot
if (-not $Metadata) { $Metadata = Join-Path $buildDir 'signing\metadata.json' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$csproj = Join-Path $root 'src\apps\Wavee\Wavee.csproj'
$iconSource = Join-Path $root 'src\apps\Wavee\assets\AppIcon\appicon-source.png'
$manifestTemplate = Join-Path $buildDir 'Wavee.AppxManifest.xml'
$rid = "win-$Arch"
$stamp = "Wavee_${Version}_${Arch}"
$work = Join-Path $root ".msix-build\wavee-$Arch"
$pubDir = Join-Path $work 'publish'
$layout = Join-Path $work 'layout'
$outRoot = Join-Path $root $OutputDir
$outMsix = Join-Path $outRoot "$stamp.msix"
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

$kitsBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
$sdkVer = Get-ChildItem $kitsBin -Directory -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -match '^10\.' -and (Test-Path (Join-Path $_.FullName 'x64\makeappx.exe')) } |
          Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $sdkVer) { throw "No Windows SDK with makeappx.exe found under $kitsBin. Install the Windows SDK." }
$toolDir = Join-Path $sdkVer.FullName 'x64'
$makeappx = Join-Path $toolDir 'makeappx.exe'
$makepri = Join-Path $toolDir 'makepri.exe'
$signtool = Join-Path $toolDir 'signtool.exe'
Step "SDK $($sdkVer.Name)"

$vsInstaller = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if ((Test-Path "$vsInstaller\vswhere.exe") -and ($env:PATH -notlike "*$vsInstaller*")) { $env:PATH = "$vsInstaller;$env:PATH" }

$useAot = -not $NoAot

Step "Publishing $rid ($(if ($useAot) { 'NativeAOT' } else { 'self-contained JIT' }))"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $pubDir, $outRoot | Out-Null
$pubArgs = @($csproj, '-c', $Configuration, '-r', $rid, '-o', $pubDir, '--nologo', '-v', 'm', '/p:NuGetAudit=false')
if (-not $useAot) { $pubArgs += @('-p:PublishAot=false', '--self-contained', 'true') }
& dotnet publish @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
if (-not (Test-Path (Join-Path $pubDir 'Wavee.exe'))) { throw "Wavee.exe missing from $pubDir" }

Step "Staging package layout"
New-Item -ItemType Directory -Force -Path $layout | Out-Null
Copy-Item "$pubDir\*" $layout -Recurse -Force
Get-ChildItem $layout -Recurse -Include *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue

$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
if (-not (Test-Path $iconSource)) { throw "missing Wavee icon source: $iconSource" }
Add-Type -AssemblyName System.Drawing
function New-WaveeLogo([int]$w, [int]$h, [string]$path) {
  $src = [System.Drawing.Image]::FromFile($script:iconSource)
  try {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::FromArgb(255, 13, 17, 42))
    $side = [Math]::Min($w, $h)
    $dx = [int](($w - $side) / 2)
    $dy = [int](($h - $side) / 2)
    $g.DrawImage($src, $dx, $dy, $side, $side)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
  }
  finally { $src.Dispose() }
}
New-WaveeLogo 50 50 (Join-Path $assets 'StoreLogo.png')
New-WaveeLogo 44 44 (Join-Path $assets 'Square44x44Logo.png')
New-WaveeLogo 71 71 (Join-Path $assets 'Square71x71Logo.png')
New-WaveeLogo 150 150 (Join-Path $assets 'Square150x150Logo.png')
New-WaveeLogo 310 310 (Join-Path $assets 'Square310x310Logo.png')
New-WaveeLogo 310 150 (Join-Path $assets 'Wide310x150Logo.png')

$mf = (Get-Content $manifestTemplate -Raw).Replace('__PUBLISHER__', $Publisher).Replace('__VERSION__', $Version).Replace('__ARCH__', $Arch)
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $mf, $utf8NoBom)

Step "Generating resources.pri"
$priConfig = Join-Path $work 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
Push-Location $layout
try { & $makepri new /pr $layout /cf $priConfig /of (Join-Path $layout 'resources.pri') /o | Out-Null }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "makepri failed ($LASTEXITCODE)." }

Step "Packing $outMsix"
Remove-Item $outMsix -Force -ErrorAction SilentlyContinue
& $makeappx pack /o /d $layout /p $outMsix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed ($LASTEXITCODE)." }

$cerPath = Join-Path $outRoot "$stamp.cer"
if (-not $NoSign) {
  if ($TrustedSigning) {
    Step "Signing with Azure Trusted Signing"
    if (-not (Test-Path $Metadata)) { throw "Trusted Signing metadata not found: $Metadata" }
    $dlib = @(
      "$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll",
      'C:\Program Files (x86)\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
      'C:\Program Files\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
      'C:\Program Files (x86)\Microsoft\TrustedSigningClientTools\bin\Azure.CodeSigning.Dlib.dll'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $dlib) { throw "Azure.CodeSigning.Dlib.dll not found." }
    if (-not ($env:AZURE_CLIENT_ID -and $env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_SECRET)) {
      if ($Subscription) { & az account set --subscription $Subscription 2>$null }
    }
    & $signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $dlib /dmdf $Metadata $outMsix
    if ($LASTEXITCODE -ne 0) { throw "Trusted Signing failed ($LASTEXITCODE)." }
    if ($Install) { Add-AppxPackage -Path $outMsix }
  }
  else {
    Step "Signing with a self-signed cert ($Publisher)"
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if (-not $cert) {
      $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Publisher `
                -KeyUsage DigitalSignature -FriendlyName 'Wavee Dev Signing' `
                -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) `
                -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
    }
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $outMsix
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed ($LASTEXITCODE)." }
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    if ($Install) {
      try { Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null }
      catch { Write-Warning "Could not trust the cert (run elevated). Sideload may prompt." }
      Add-AppxPackage -Path $outMsix
    }
  }
}

$size = [Math]::Round((Get-Item $outMsix).Length / 1MB, 1)
Step "Done"
Write-Host "    $outMsix  (${size} MB, $Arch, v$Version$(if ($useAot) { ', AOT' } else { ', JIT' })$(if ($NoSign) { ', UNSIGNED' }))" -ForegroundColor Green
if (Test-Path $cerPath) {
  Write-Host "    cert: $cerPath"
  Write-Host "    On Shadow (elevated once):"
  Write-Host "      Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
  Write-Host "      Add-AppxPackage -Path '$outMsix'"
}
