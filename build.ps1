$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bridge = Join-Path $root 'bridge'
Write-Host 'Restoring and building DynamicIslandBridge...' -ForegroundColor Cyan
dotnet restore (Join-Path $bridge 'DynamicIslandBridge.csproj')
dotnet build (Join-Path $bridge 'DynamicIslandBridge.csproj') -c Release --no-restore
Write-Host 'Running protocol tests...' -ForegroundColor Cyan
python (Join-Path $root 'tests/test_static_contract.py')
python (Join-Path $root 'tests/test_bridge_protocol.py')
$out = Join-Path $root 'artifacts/DynamicIslandBridge'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $bridge 'DynamicIslandBridge.csproj') -c Release -r win-x64 --self-contained false --no-restore -o $out
Copy-Item (Join-Path $root 'sourcecode') $out
Write-Host "Release package written to $out" -ForegroundColor Green
