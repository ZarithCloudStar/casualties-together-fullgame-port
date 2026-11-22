param([ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Phase = (Get-Date -Format 'yyyyMMdd-HHmmss'))
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repoRoot "build\sandbox-verification\$Phase"
if (Test-Path -LiteralPath $output) { throw "Use a new phase: $output already exists." }
$copies = @('F:\cu-port\CU_Fullgame', 'F:\cu-port\CU_Fullgame_B')
if (Get-Process -Name CasualtiesUnknown -ErrorAction SilentlyContinue | Where-Object { (Split-Path $_.Path -Parent) -in $copies }) {
    throw 'Close the A/B test copies before running the fixture.'
}
$testDll = Join-Path $repoRoot 'build\sandbox-playtest\CasualtiesMP.dll'
if (!(Test-Path -LiteralPath $testDll)) { throw 'Build PlaytestFullgame with ModPluginOutputDir=build\sandbox-playtest first.' }
New-Item -ItemType Directory -Force $output | Out-Null
$launched = @()
$backups = @{}
$previousSave = $env:CT_ENDING_TEST_SAVE
function Read-LiveLog([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) { return '' }
    $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    $reader = [IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
try {
    foreach ($copy in $copies) {
        $destination = Join-Path $copy 'BepInEx\plugins\Together\CasualtiesMP.dll'
        $backup = Join-Path $output ((Split-Path $copy -Leaf) + '-before.dll')
        Copy-Item -LiteralPath $destination -Destination $backup
        $backups[$destination] = $backup
        Copy-Item -LiteralPath $testDll -Destination $destination
    }
    $env:CT_ENDING_TEST_SAVE = Join-Path $output 'fixture-save'
    $common = '--mp-nosteam --mp-ending-test --mp-sandbox-test --mp-ip-port 127.0.0.1:7796 --mp-charsel-timeout 0 -screen-fullscreen 0 -screen-width 1000 -screen-height 720'
    $launched += Start-Process -FilePath (Join-Path $copies[0] 'CasualtiesUnknown.exe') -WorkingDirectory $copies[0] -WindowStyle Hidden -ArgumentList "$common --mp-starthost --mp-setname SandboxHost --mp-setcharacter 0 -logFile $output\host.log" -PassThru
    $launched += Start-Process -FilePath (Join-Path $copies[1] 'CasualtiesUnknown.exe') -WorkingDirectory $copies[1] -WindowStyle Hidden -ArgumentList "$common --mp-startclient --mp-setname SandboxClient -logFile $output\client.log" -PassThru
    ($launched.Id -join ',') | Set-Content (Join-Path $output 'pids.txt')
    $deadline = [DateTime]::UtcNow.AddSeconds(140)
    do {
        Start-Sleep -Seconds 2
        $hostLog = Read-LiveLog (Join-Path $output 'host.log')
        $clientLog = Read-LiveLog (Join-Path $output 'client.log')
        if (($hostLog + $clientLog).Contains('[sandbox-test] FAIL')) { throw "Sandbox fixture failed; see $output" }
        if ($hostLog.Contains('[sandbox-test] complete side=host') -and $clientLog.Contains('[sandbox-test] complete side=client')) {
            'PASS' | Set-Content (Join-Path $output 'result.txt')
            Write-Output "PASS: $output"
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Sandbox fixture timed out; see $output"
} finally {
    foreach ($process in $launched) {
        if (!$process.HasExited -and (Split-Path $process.Path -Parent) -in $copies) {
            Stop-Process -InputObject $process
            $process.WaitForExit(10000) | Out-Null
        }
    }
    foreach ($destination in $backups.Keys) { Copy-Item -LiteralPath $backups[$destination] -Destination $destination }
    if ($null -eq $previousSave) { Remove-Item Env:\CT_ENDING_TEST_SAVE -ErrorAction SilentlyContinue }
    else { $env:CT_ENDING_TEST_SAVE = $previousSave }
}
