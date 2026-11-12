param([string[]]$Scenarios = @('merc', 'escape-timeout', 'escape-down', 'escape-killed'))
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$results = @()
function Read-LiveLog([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) { return '' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
foreach ($scenario in $Scenarios) {
    $phase = "regression-$scenario"
    & (Join-Path $PSScriptRoot 'Test-EndingFlow.ps1') -Phase $phase -Scenario $scenario -AutoNarrative
    $output = Join-Path $repoRoot "build\ending-tests\$phase"
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    $passed = $false
    try {
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Seconds 2
            $hostText = Read-LiveLog "$output\host.log"
            $clientText = Read-LiveLog "$output\client.log"
            if (($hostText + $clientText).Contains('[ending-test] FAIL')) { break }
            $clientPassed = $clientText.Contains('client local metaprogress') -or $clientText.Contains('client Merc metaprogress')
            if ($scenario -eq 'disconnect') { $clientPassed = $clientText.Contains('[ending-test] PASS client owner replicated') }
            if ($scenario -eq 'reload') { $clientPassed = $clientText.Contains('[ending-test] reload fixture complete side=client') }
            if ($hostText.Contains("[ending-test] $scenario fixture complete") -and $clientPassed) {
                $passed = $true; break
            }
        }
    } finally {
        foreach ($testPid in ([IO.File]::ReadAllText("$output\pids.txt").Trim().Split(','))) {
            $process = Get-Process -Id ([int]$testPid) -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -eq 'F:\cu-port\CU_Fullgame\CasualtiesUnknown.exe' -or $_.Path -eq 'F:\cu-port\CU_Fullgame_B\CasualtiesUnknown.exe' }
            if ($process) { Stop-Process -InputObject $process; $process.WaitForExit(10000) | Out-Null }
        }
    }
    $results += [pscustomobject]@{ scenario = $scenario; passed = $passed; log = $output }
    Write-Output "$scenario passed=$passed"
    $results | ConvertTo-Json | Set-Content (Join-Path $repoRoot 'build\ending-tests\regression-results.json')
}
