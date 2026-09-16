$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $MyInvocation.MyCommand.Path
$destination = Join-Path $env:LOCALAPPDATA 'DynamicIslandBridge'
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item (Join-Path $package 'DynamicIslandBridge.exe') $destination -Force
Get-ChildItem $package -Filter '*.dll' | Copy-Item -Destination $destination -Force
Start-Process (Join-Path $destination 'DynamicIslandBridge.exe') -ArgumentList '--setup'
Write-Host "Dynamic Island bridge installed to $destination and setup wizard started."
Write-Host "Next: use Windhawk Create new mod and paste the sourcecode file."
