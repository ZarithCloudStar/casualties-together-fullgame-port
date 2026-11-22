param(
    [ValidateSet('full-route','full-route-host')][string]$Scenario = 'full-route',
    [ValidatePattern('^[a-z0-9_-]+$')][string]$Phase = ('port-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [int]$TimeoutSeconds = 360
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repoRoot "build\ending-tests\$Phase"
if (Test-Path -LiteralPath $output) { throw "Use a fresh phase directory: $output" }
$copies = @('F:\cu-port\CU_Fullgame\CasualtiesUnknown.exe','F:\cu-port\CU_Fullgame_B\CasualtiesUnknown.exe')
if (Get-Process CasualtiesUnknown -ErrorAction SilentlyContinue | Where-Object Path -In $copies) {
    throw 'Close the existing A/B test instances before running this fixture.'
}
$passed = $false
$started = Get-Date
try {
    & (Join-Path $PSScriptRoot 'Test-EndingFlow.ps1') -Phase $Phase -Scenario $Scenario -AutoNarrative
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path "$output\server.done") -and (Test-Path "$output\client.done")) { $passed = $true; break }
        $failures = Get-ChildItem -LiteralPath $output -Filter '*.log' | Select-String -Pattern '\[route-test\] FAIL'
        if ($failures) { throw ($failures.Line -join "`n") }
        Start-Sleep -Seconds 2
    }
    if (!$passed) { throw "Timed out; inspect $output" }
} finally {
    $pidFile = Join-Path $output 'pids.txt'
    if (Test-Path -LiteralPath $pidFile) {
        foreach ($testId in ([IO.File]::ReadAllText($pidFile).Trim().Split(','))) {
            $process = Get-Process -Id ([int]$testId) -ErrorAction SilentlyContinue
            if ($process -and $process.Path -in $copies) { Stop-Process -InputObject $process }
        }
    }
    [pscustomobject]@{scenario=$Scenario; passed=$passed; seconds=((Get-Date)-$started).TotalSeconds; logs=$output} |
        ConvertTo-Json | Set-Content (Join-Path $output 'result.json')
}
Write-Output "PASS $Scenario; logs and screenshots: $output"
