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

Get-ChildItem $toolsDir | Where-Object { $_.Name -match '\.(exe|dll)$' -or $_.Name -eq 'inklecate' } | Select-Object Name, Length
Write-Host "Done."
