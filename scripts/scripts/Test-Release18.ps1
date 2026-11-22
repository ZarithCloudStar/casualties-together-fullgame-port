param(
    [string[]]$Scenarios = @('r14-escape','r14-remote','r14-noncoredeath','r14-god-assist','r18-layer10'),
    [ValidatePattern('^[a-z0-9_-]+$')][string]$Run = ('release18-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [int]$TimeoutSeconds = 300,
    [switch]$IsolatedLoader,
    [int]$Port = 7790
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-Release14.ps1') -Scenarios $Scenarios -Run $Run -TimeoutSeconds $TimeoutSeconds -IsolatedLoader:$IsolatedLoader -Port $Port
$repoRoot = Split-Path $PSScriptRoot -Parent
$results = Get-Content -LiteralPath (Join-Path $repoRoot "build\$Run-results.json") -Raw | ConvertFrom-Json
if ($results | Where-Object { !$_.passed }) { throw 'Release18 runtime regression failed; see result JSON and host/client logs.' }
