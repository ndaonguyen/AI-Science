# Download the sqlite-vec native extension (vec0.dll) for Windows into ~/.dd
# Mirror of bootstrap-vec.sh for non-bash environments. Run once after
# cloning.
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts/bootstrap-vec.ps1

$ErrorActionPreference = 'Stop'

$Version = 'v0.1.6'
$TargetDir = Join-Path $env:USERPROFILE '.dd'
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null

$Arch = if ([Environment]::Is64BitOperatingSystem) { 'x86_64' } else { 'x86' }
$VersionNoV = $Version.TrimStart('v')
$Asset = "sqlite-vec-$VersionNoV-loadable-windows-$Arch.tar.gz"
$Url = "https://github.com/asg017/sqlite-vec/releases/download/$Version/$Asset"

Write-Host "→ fetching $Url"
$TmpDir = Join-Path $env:TEMP "dd-vec-$(Get-Random)"
New-Item -ItemType Directory -Force -Path $TmpDir | Out-Null
try {
    $TarPath = Join-Path $TmpDir $Asset
    Invoke-WebRequest -Uri $Url -OutFile $TarPath -UseBasicParsing

    Write-Host "→ extracting"
    # tar.exe is bundled with Windows 10 1803+; no extra dependency.
    tar -xzf $TarPath -C $TmpDir

    $Lib = Get-ChildItem -Path $TmpDir -Recurse -Filter 'vec0.dll' | Select-Object -First 1
    if (-not $Lib) {
        throw "Couldn't find vec0.dll in the tarball — sqlite-vec may have changed its asset layout. Inspect $TmpDir manually."
    }

    $Dest = Join-Path $TargetDir 'vec0.dll'
    Copy-Item -Path $Lib.FullName -Destination $Dest -Force
    Write-Host "✓ installed $Dest"
} finally {
    Remove-Item -Recurse -Force -Path $TmpDir -ErrorAction SilentlyContinue
}
