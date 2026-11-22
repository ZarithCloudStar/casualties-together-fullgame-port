param([string]$Run = 'fixed', [int]$Port = 7861, [switch]$Legacy, [switch]$Layer2,
      [string]$SeedSave, [switch]$LateJoin, [switch]$Coverage, [switch]$Positions, [switch]$Minimal,
      [ValidateSet('','sequence','checksum','truncate','template','version','timeout')][string]$Fault = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo ("build\world-checkpoint-tests\$Run-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force (Join-Path $output 'save') | Out-Null
if ($SeedSave) {
    Get-ChildItem -LiteralPath $SeedSave | Copy-Item -Destination (Join-Path $output 'save') -Recurse
    Get-ChildItem -LiteralPath (Join-Path $output 'save') -File | Where-Object Name -Match '^(host[.-]|client[.-]|server-|result\.|capture-at|failure-evidence|screenshot-comparison|coverage-stage|coverage-host|coverage-client|placement-client-restored|placement-host-restored|placement-next-layer|placement-client-next-layer)' | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
}
$runtime = Join-Path $repo 'build\ending-tests\release19-verified-final-runtime'
$launched = @()
$previousSave = $env:CT_ENDING_TEST_SAVE
try {
    $env:CT_ENDING_TEST_SAVE = Join-Path $output 'save'
    foreach ($side in @('A','B')) {
        $loader = Join-Path $output "runtime-$side"
        $source = 'F:\cu-port\CU_Fullgame\BepInEx'
        New-Item -ItemType Directory -Force (Join-Path $loader 'plugins\Together') | Out-Null
        foreach ($part in @('core','config','patchers')) { Copy-Item -LiteralPath (Join-Path $source $part) -Destination $loader -Recurse }
        Get-ChildItem (Join-Path $source 'plugins\Together') -File | Where-Object Name -NE 'CasualtiesMP.dll' | Copy-Item -Destination (Join-Path $loader 'plugins\Together')
        Copy-Item -LiteralPath (Join-Path $repo 'build\bugfix-PlaytestFullgame\CasualtiesMP.dll') -Destination (Join-Path $loader 'plugins\Together\CasualtiesMP.dll')
        $game = Join-Path $runtime "game-$side\CT_EndingTest_$side.exe"
        $role = if ($side -eq 'A') { '--mp-starthost --mp-setname CheckpointHost --mp-setcharacter 0' } else { '--mp-startclient --mp-setname CheckpointGuest' }
        $common = "--mp-nosteam --mp-ending-test --mp-world-checkpoint-test --mp-charsel-timeout 0 --mp-ip-port 127.0.0.1:$Port -screen-fullscreen 0 -screen-width 800 -screen-height 600"
        if ($Legacy) { $common += ' --mp-world-checkpoint-legacy' }
        if ($Layer2) { $common += ' --mp-world-checkpoint-layer2' }
        if ($Coverage) { $common += ' --mp-structure-coverage' }
        if ($Positions) { $common += ' --mp-player-placement-test' }
        if ($Minimal) { $common += ' --mp-fix7-minimal' }
        if ($SeedSave) { $common += ' --mp-world-checkpoint-loadonly' }
        if ($Fault) { $common += " --mp-world-snapshot-fault=$Fault" }
        if ($LateJoin -and $side -eq 'B') {
            $hostDeadline = [DateTime]::UtcNow.AddSeconds(120)
            do {
                Start-Sleep -Seconds 1
                $hostPath = Join-Path $output 'A.log'
                if (Test-Path $hostPath) {
                    $hostStream = [IO.File]::Open($hostPath, 'Open', 'Read', 'ReadWrite')
                    $hostReader = [IO.StreamReader]::new($hostStream)
                    try { $hostLog = $hostReader.ReadToEnd() } finally { $hostReader.Dispose() }
                    if ($hostLog.Contains('WorldGeneration.FinishWorldGeneration')) { break }
                }
            } while ([DateTime]::UtcNow -lt $hostDeadline)
            if (!$hostLog.Contains('WorldGeneration.FinishWorldGeneration')) { throw "Host load timeout: $output" }
        }
        $arguments = "$common $role --doorstop-target-assembly `"$loader\core\BepInEx.Preloader.dll`" -logFile `"$output\$side.log`""
        $launched += Start-Process -FilePath $game -WorkingDirectory (Split-Path $game -Parent) -WindowStyle Hidden -ArgumentList $arguments -PassThru
    }
    "Started $Run; logs: $output"
    $deadline = [DateTime]::UtcNow.AddSeconds(360)
    do {
        Start-Sleep -Seconds 2
        $path = Join-Path $output 'A.log'
        if (!(Test-Path $path)) { continue }
        $stream = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
        $reader = [IO.StreamReader]::new($stream)
        try { $log = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $clientPath = Join-Path $output 'B.log'
        if (Test-Path $clientPath) {
            $clientStream = [IO.File]::Open($clientPath, 'Open', 'Read', 'ReadWrite')
            $clientReader = [IO.StreamReader]::new($clientStream)
            try { $clientLog = $clientReader.ReadToEnd() } finally { $clientReader.Dispose() }
        } else { $clientLog = '' }
        if ($Fault -and (Test-Path (Join-Path $output 'save\failure-evidence.json'))) {
            $evidence = Get-Content -LiteralPath (Join-Path $output 'save\failure-evidence.json') -Raw | ConvertFrom-Json
            $expected = @{ sequence='sequence/length'; checksum='digest'; truncate='digest'; template='template unavailable'; version='version mismatch'; timeout='receive timeout' }[$Fault]
            if ($evidence.applied -or $evidence.restoredObjects -ne 0 -or $evidence.reason -notlike "*$expected*" -or $log.Contains('spawn unlocked')) { throw "Unsafe/wrong fault outcome: $output" }
            "PASS expected $Fault rejection before mutation/spawn" | Set-Content -LiteralPath (Join-Path $output 'save\result.txt')
            "PASS expected $Fault failure before world mutation/spawn: $output"; return
        }
        if ($log.Contains('[world-checkpoint-test] FAIL') -or $clientLog.Contains('[world-checkpoint-test] FAIL') -or $log.Contains('[placement-test] FAIL') -or $clientLog.Contains('[placement-test] FAIL') -or $log.Contains('[structure-coverage] FAIL') -or $clientLog.Contains('[structure-coverage] FAIL')) { throw "Fixture failed: $output" }
        if ($log.Contains('[world-checkpoint-test] complete')) { "PASS: $output"; return }
        if (@($launched | Where-Object { !$_.HasExited }).Count -eq 0) { throw "Both isolated instances exited: $output" }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Fixture timeout: $output"
} finally {
    foreach ($process in $launched) {
        if (!$process.HasExited) { Stop-Process -InputObject $process; $process.WaitForExit(5000) | Out-Null }
    }
    $env:CT_ENDING_TEST_SAVE = $previousSave
}
