#!/usr/bin/env pwsh
# Downloads the inklecate v1.2.1 release for the current OS into this folder.
# The csproj references tools/ink-engine-runtime.dll directly, so this must run
# before `dotnet build` on a fresh clone.

$ErrorActionPreference = 'Stop'

$version = 'v1.2.1'
if ($IsWindows -or ($PSVersionTable.PSVersion.Major -lt 6)) {
    $asset = 'inklecate_windows.zip'
} elseif ($IsMacOS) {
    $asset = 'inklecate_mac.zip'
} else {
    $asset = 'inklecate_linux.zip'
}
$url = "https://github.com/inkle/ink/releases/download/$version/$asset"
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$zip = Join-Path $toolsDir $asset

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip

Write-Host "Extracting"
Expand-Archive -Path $zip -DestinationPath $toolsDir -Force
Remove-Item $zip

if (-not $IsWindows -and ($PSVersionTable.PSVersion.Major -ge 6)) {
    $bin = Join-Path $toolsDir 'inklecate'
    if (Test-Path $bin) { chmod +x $bin }
}

# ── esbuild (for transpiling Game/*.ts combat scripts) ──────────────────────
$esbuildVersion = '0.28.0'
if ($IsWindows -or ($PSVersionTable.PSVersion.Major -lt 6)) {
    $pkg = 'win32-x64'; $binInPkg = 'package/esbuild.exe'; $binOut = 'esbuild.exe'
} elseif ($IsMacOS) {
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $pkg = "darwin-$arch"; $binInPkg = 'package/bin/esbuild'; $binOut = 'esbuild'
} else {
    $pkg = 'linux-x64'; $binInPkg = 'package/bin/esbuild'; $binOut = 'esbuild'
}
$tgz = Join-Path $toolsDir "esbuild-$pkg.tgz"
Write-Host "Downloading esbuild $esbuildVersion ($pkg)"
Invoke-WebRequest -Uri "https://registry.npmjs.org/@esbuild/$pkg/-/$pkg-$esbuildVersion.tgz" -OutFile $tgz
tar -xzf $tgz -C $toolsDir $binInPkg
Move-Item -Force (Join-Path $toolsDir $binInPkg) (Join-Path $toolsDir $binOut)
Remove-Item -Recurse -Force (Join-Path $toolsDir 'package')
Remove-Item $tgz
if (-not $IsWindows -and ($PSVersionTable.PSVersion.Major -ge 6)) { chmod +x (Join-Path $toolsDir $binOut) }

Get-ChildItem $toolsDir | Where-Object { $_.Name -match '\.(exe|dll)$' -or $_.Name -in @('inklecate', 'esbuild') } | Select-Object Name, Length
Write-Host "Done."
