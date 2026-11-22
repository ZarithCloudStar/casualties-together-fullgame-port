param(
    [ValidatePattern('^[a-z0-9_-]+$')][string]$Run = ('escape-ready-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [switch]$SkipBuild,
    [int]$TimeoutSeconds = 240,
    [ValidateRange(1024,65535)][int]$Port = 7799
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if ([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveUdpListeners() | Where-Object Port -EQ $Port) { throw "UDP port $Port is already in use; close that test session first or choose a free -Port." }
$output = Join-Path $repoRoot "build\ending-tests\$Run"
if (Test-Path -LiteralPath $output) { throw "Choose a fresh -Run name: $output" }
if (!$SkipBuild) {
    $oldPackages = $env:NUGET_PACKAGES
    try {
        $env:NUGET_PACKAGES = 'F:\cu-port\.nuget'
        $buildLog = Join-Path $repoRoot 'build\release18-start-build.log'
        & E:\tools\dotnet\dotnet.exe build (Join-Path $repoRoot 'ScavPrototypeMP\CasualtiesTogether.csproj') -c PlaytestFullgame "-p:SolutionDir=$repoRoot\" "-p:ModPluginOutputDir=$repoRoot\build\ending-playtest" *> $buildLog
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $buildLog" }
    } finally { $env:NUGET_PACKAGES = $oldPackages }
}
& (Join-Path $PSScriptRoot 'Test-EndingFlow.ps1') -Phase $Run -Scenario r18-ready -Interactive -IsolatedLoader -Port $Port
$processIds = ([IO.File]::ReadAllText((Join-Path $output 'pids.txt'))).Trim().Split(',')
Write-Output "A (host) PID=$($processIds[0]) log=$output\host.log"
Write-Output "B (client) PID=$($processIds[1]) log=$output\client.log"
Write-Output 'Preparing a fresh isolated test save; character confirmation and world entry are automatic.'
Write-Output "Using isolated BepInEx loaders in $output ; installed game DLLs and existing sessions are untouched. UDP=$Port"
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while ([DateTime]::UtcNow -lt $deadline) {
    $failures = Get-ChildItem -LiteralPath $output -Filter '*.log' | Select-String -Pattern '\[r18-test\] FAIL|^(NullReferenceException|MissingReferenceException|InvalidOperationException|Exception):'
    if ($failures) { throw ($failures.Line -join "`n") }
    if ((Test-Path "$output\server.ready") -and (Test-Path "$output\client.ready")) {
        Write-Output 'READY: A chipped, shotgun + boxof12gauge, godmode; B unchipped, cargo carrier. Both on the real layer11 entrance floor at x=22/23 before the x<17 trigger.'
        Write-Output 'Manual: walk left across x<17 to start the scientist scene, then complete dialogue and shoot the scientist. Godmode is the existing server command and protects both players.'
        Write-Output "Screenshots: $output\host-escape-ready.png ; $output\client-escape-ready.png"
        return
    }
    Start-Sleep -Seconds 2
}
throw "Setup timed out; A/B remain open for inspection. Logs: $output"
