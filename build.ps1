$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bridge = Join-Path $root 'bridge'
$project = Join-Path $bridge 'DynamicIslandBridge.csproj'
$out = Join-Path $root 'artifacts/DynamicIslandBridge'

function Invoke-Checked {
    param([scriptblock]$Command, [string]$Name)
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

Write-Host 'Restoring and building DynamicIslandBridge...' -ForegroundColor Cyan
Invoke-Checked { dotnet restore $project } 'dotnet restore'
Invoke-Checked { dotnet build $project -c Release --no-restore } 'dotnet build'

Write-Host 'Running protocol tests...' -ForegroundColor Cyan
$env:PYTHONUTF8 = '1'
Invoke-Checked { python (Join-Path $root 'tests/test_static_contract.py') } 'static contract tests'
Invoke-Checked { python (Join-Path $root 'tests/test_bridge_protocol.py') } 'bridge protocol tests'
Invoke-Checked { python (Join-Path $root 'tests/test_setup_contract.py') } 'setup contract tests'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null
Invoke-Checked { dotnet publish $project -c Release -r win-x64 --self-contained false --no-restore -o $out } 'dotnet publish'
Copy-Item (Join-Path $root 'sourcecode') $out
Write-Host "Release package written to $out" -ForegroundColor Green
