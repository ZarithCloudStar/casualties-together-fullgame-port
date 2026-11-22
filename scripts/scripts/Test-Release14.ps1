param(
    [string[]]$Scenarios = @('r14-merc','r14-escape','r14-remote','r14-coredeath','r14-capture','r14-noncoredeath','r14-chipped','r14-timeout'),
    [string]$Run = ('r14-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [int]$TimeoutSeconds = 240,
    [switch]$IsolatedLoader,
    [int]$Port = 7790
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$copies = @('F:\cu-port\CU_Fullgame\CasualtiesUnknown.exe','F:\cu-port\CU_Fullgame_B\CasualtiesUnknown.exe')
if (!$IsolatedLoader -and (Get-Process CasualtiesUnknown -ErrorAction SilentlyContinue | Where-Object Path -In $copies)) { throw 'A/B instances already running; refusing to disturb them.' }
$results = @()
foreach ($scenario in $Scenarios) {
    $phase = "$Run-$scenario"
    $output = Join-Path $repoRoot "build\ending-tests\$phase"
    if (Test-Path -LiteralPath $output) { throw "Use a fresh output directory: $output" }
    $passed = $false
    $reason = ''
    $started = Get-Date
    $dllHash = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'build\ending-playtest\CasualtiesMP.dll')).Hash
    try {
        & (Join-Path $PSScriptRoot 'Test-EndingFlow.ps1') -Phase $phase -Scenario $scenario -IsolatedLoader:$IsolatedLoader -Port $Port -AutoNarrative
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ((Test-Path "$output\server.done") -and (Test-Path "$output\client.done")) { $passed = $true; break }
            $failures = Get-ChildItem -LiteralPath $output -Filter '*.log' | Select-String -Pattern '\[r(?:14|18)-test\] FAIL|^(NullReferenceException|MissingReferenceException|IndexOutOfRangeException|ArgumentOutOfRangeException|InvalidOperationException|Exception):'
            if ($failures) { $reason = ($failures.Line -join "`n"); break }
            Start-Sleep -Seconds 2
        }
        if (!$passed -and !$reason) { $reason = 'timeout' }
    } finally {
        $pidFile = Join-Path $output 'pids.txt'
        if (Test-Path -LiteralPath $pidFile) {
            foreach ($testId in ([IO.File]::ReadAllText($pidFile).Trim().Split(','))) {
                $process = Get-Process -Id ([int]$testId) -ErrorAction SilentlyContinue
                if ($process -and $process.Path -in $copies) { Stop-Process -InputObject $process; $process.WaitForExit(10000) | Out-Null }
            }
        }
        $result = [pscustomobject]@{scenario=$scenario; passed=$passed; reason=$reason; dllSha256=$dllHash; seconds=((Get-Date)-$started).TotalSeconds; logs=$output}
        $result | ConvertTo-Json | Set-Content (Join-Path $output 'result.json')
        $results += $result
        $results | ConvertTo-Json | Set-Content (Join-Path $repoRoot "build\$Run-results.json")
        Write-Output "$scenario passed=$passed $reason"
    }
}
