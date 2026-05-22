#!/usr/bin/env pwsh
# Downloads the inklecate v1.2.1 Windows release into this folder.
# The csproj references tools\ink-engine-runtime.dll directly, so this must run
# before `dotnet build` on a fresh clone.

$ErrorActionPreference = 'Stop'

$version = 'v1.2.1'
$url = "https://github.com/inkle/ink/releases/download/$version/inklecate_windows.zip"
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$zip = Join-Path $toolsDir 'inklecate_windows.zip'

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip

Write-Host "Extracting"
Expand-Archive -Path $zip -DestinationPath $toolsDir -Force
Remove-Item $zip

Get-ChildItem $toolsDir -Filter '*.exe', '*.dll' | Select-Object Name, Length
Write-Host "Done."
