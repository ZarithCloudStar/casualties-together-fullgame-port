param(
    [ValidatePattern('^[a-z0-9_-]+$')][string]$Run = 'release19-final',
    [string[]]$Scenarios = @('r19-noncore-host','r19-noncore-client','r19-core-host','r19-core-client','r19-merc'),
    [int]$Port = 7830
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$lockPath = Join-Path $repoRoot 'build\ending-tests\release19-run.lock'
New-Item -ItemType Directory -Force (Split-Path $lockPath -Parent) | Out-Null
try { $runLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
catch { throw 'Another Test-Release19 run is active. Let it finish before starting a second A/B regression.' }
try {
$snapshot = Join-Path $repoRoot "build\ending-tests\$Run-runtime"
if (Test-Path -LiteralPath $snapshot) { throw "Use a fresh run name: $snapshot" }
New-Item -ItemType Directory -Force $snapshot | Out-Null
$testDll = Join-Path $snapshot 'CasualtiesMP.dll'
Copy-Item -LiteralPath (Join-Path $repoRoot 'build\ending-playtest\CasualtiesMP.dll') -Destination $testDll
$sourceSnapshot = @('EndingFlow.Escape.cs','EndingFlow.Narrative.cs','EndingFlowTests.cs','Release14EndingTests.cs','Release19EndingTests.cs') | ForEach-Object {
    $sourcePath = Join-Path $repoRoot "ScavPrototypeMP\Compatibility\$_"
    [pscustomobject]@{ path=$sourcePath; sha256=(Get-FileHash -LiteralPath $sourcePath).Hash }
}
$sourceSnapshot += [pscustomobject]@{ path=(Join-Path $repoRoot 'ScavPrototypeMP\Plugin.cs'); sha256=(Get-FileHash -LiteralPath (Join-Path $repoRoot 'ScavPrototypeMP\Plugin.cs')).Hash }
$sourceSnapshot | ConvertTo-Json | Set-Content (Join-Path $snapshot 'source-sha256.json')
$gameExecutables = @()
foreach ($side in @('A','B')) {
    $source = if ($side -eq 'A') { 'F:\cu-port\CU_Fullgame' } else { 'F:\cu-port\CU_Fullgame_B' }
    $gameDir = Join-Path $snapshot "game-$side"
    $gameName = "CT_EndingTest_$side"
    New-Item -ItemType Directory -Force $gameDir | Out-Null
    Get-ChildItem -LiteralPath $source -File | Where-Object Name -NE 'CasualtiesUnknown.exe' | Copy-Item -Destination $gameDir
    Copy-Item -LiteralPath (Join-Path $source 'CasualtiesUnknown.exe') -Destination (Join-Path $gameDir "$gameName.exe")
    Copy-Item -LiteralPath (Join-Path $source 'CasualtiesUnknown_Data') -Destination (Join-Path $gameDir "${gameName}_Data") -Recurse
    Copy-Item -LiteralPath (Join-Path $source 'MonoBleedingEdge') -Destination (Join-Path $gameDir 'MonoBleedingEdge') -Recurse
    $gameExecutables += Join-Path $gameDir "$gameName.exe"
}
Write-Output "Running private copies from $snapshot ; existing A/B sessions are independent."
$results = @()
foreach ($scenario in $Scenarios) {
    $phase = "$Run-$scenario"
    $output = Join-Path $repoRoot "build\ending-tests\$phase"
    if (Test-Path -LiteralPath $output) { throw "Use a fresh run name: $output" }
    & (Join-Path $PSScriptRoot 'Test-EndingFlow.ps1') -Phase $phase -Scenario $scenario -IsolatedLoader -Port $Port -TestDll $testDll -GameA $gameExecutables[0] -GameB $gameExecutables[1]
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $passed = $false
    $failure = ''
    try {
        while ($watch.Elapsed.TotalSeconds -lt 300) {
            $changedSource = @($sourceSnapshot | Where-Object { (Get-FileHash -LiteralPath $_.path).Hash -ne $_.sha256 })
            if ($changedSource.Count) { $failure = 'Source changed during test: ' + ($changedSource.path -join ', '); break }
            $logs = @(Get-ChildItem -LiteralPath $output -Filter '*.log' | ForEach-Object {
                $stream = [IO.File]::Open($_.FullName, 'Open', 'Read', 'ReadWrite')
                $reader = [IO.StreamReader]::new($stream)
                try { $reader.ReadToEnd() } finally { $reader.Dispose() }
            }) -join "`n"
            $rawInput = [regex]::Match($logs, '\[narrative-input\] edge source=physical-(?:mouse|attack)-down[^\r\n]*')
            if ($rawInput.Success) { $failure = '[r19-test] FAIL unexpected Unity input during unattended narrative: ' + $rawInput.Value; break }
            $match = [regex]::Match($logs, '\[(?:r19|r14|ending)-test\] FAIL[^\r\n]*|^(?:NullReferenceException|MissingReferenceException|InvalidOperationException|InvalidDataException):[^\r\n]*', 'Multiline')
            if ($match.Success) { $failure = $match.Value; break }
            if ((Test-Path "$output\server.done") -and (Test-Path "$output\client.done")) { $passed = $true; break }
            Start-Sleep -Seconds 2
        }
    } finally {
        foreach ($testPid in ([IO.File]::ReadAllText("$output\pids.txt").Trim().Split(','))) {
            $process = Get-Process -Id ([int]$testPid) -ErrorAction SilentlyContinue |
                Where-Object Path -In $gameExecutables
            if ($process) { Stop-Process -InputObject $process; $process.WaitForExit(10000) | Out-Null }
        }
    }
    $results += [pscustomobject]@{
        scenario = $scenario; passed = $passed; seconds = [math]::Round($watch.Elapsed.TotalSeconds, 2)
        failure = $failure; log = $output
        sha256 = (Get-FileHash -LiteralPath "$output\runtime-A\plugins\Together\CasualtiesMP.dll").Hash
    }
    $results | ConvertTo-Json -AsArray | Set-Content (Join-Path $repoRoot "build\$Run-results.json")
    Write-Output "$scenario passed=$passed seconds=$($watch.Elapsed.TotalSeconds) $failure"
    if (!$passed) { throw "Regression failed: $output $failure" }
    $Port++
}
} finally { $runLock.Dispose() }
