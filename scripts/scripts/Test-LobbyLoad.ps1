param([ValidateSet('load-ready','load-queued')][string]$Scenario = 'load-ready', [int]$Port = 7851)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo ("build\lobby-load-tests\" + $Scenario + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $output | Out-Null
$save = Join-Path $output 'save'
Copy-Item -LiteralPath (Join-Path $repo 'build\ending-tests\release18-ready-final\save') -Destination $save -Recurse
$runtime = Join-Path $repo 'build\ending-tests\release19-verified-final-runtime'
$launched = @()
$previousSave = $env:CT_ENDING_TEST_SAVE
$previousScenario = $env:CT_ENDING_TEST_SCENARIO
try {
    $env:CT_ENDING_TEST_SAVE = $save
    $env:CT_ENDING_TEST_SCENARIO = $Scenario
    foreach ($side in @('A','B')) {
        $loader = Join-Path $output "runtime-$side"
        $source = 'F:\cu-port\CU_Fullgame\BepInEx'
        New-Item -ItemType Directory -Force (Join-Path $loader 'plugins\Together') | Out-Null
        foreach ($part in @('core','config','patchers')) { Copy-Item -LiteralPath (Join-Path $source $part) -Destination $loader -Recurse }
        Get-ChildItem (Join-Path $source 'plugins\Together') -File | Where-Object Name -NE 'CasualtiesMP.dll' | Copy-Item -Destination (Join-Path $loader 'plugins\Together')
        Copy-Item -LiteralPath (Join-Path $repo 'build\bugfix-PlaytestFullgame\CasualtiesMP.dll') -Destination (Join-Path $loader 'plugins\Together\CasualtiesMP.dll')
        $game = Join-Path $runtime "game-$side\CT_EndingTest_$side.exe"
        $role = if ($side -eq 'A') { '--mp-starthost --mp-setname EndingHost --mp-setcharacter 0' } else { '--mp-startclient --mp-setname LoadNewGuest' }
        $common = "--mp-nosteam --mp-ending-test --mp-lobby-load-test --mp-charsel-timeout 0 --mp-ip-port 127.0.0.1:$Port -screen-fullscreen 0 -screen-width 800 -screen-height 600"
        $arguments = "$common $role --doorstop-target-assembly `"$loader\core\BepInEx.Preloader.dll`" -logFile `"$output\$side.log`""
        $launched += Start-Process -FilePath $game -WorkingDirectory (Split-Path $game -Parent) -WindowStyle Hidden -ArgumentList $arguments -PassThru
    }
    ($launched.Id -join ',') | Set-Content (Join-Path $output 'pids.txt')
    "Started $Scenario; logs: $output"
    $deadline = [DateTime]::UtcNow.AddSeconds(175)
    do {
        Start-Sleep -Seconds 2
        $logs = @('A','B') | ForEach-Object {
            $path = Join-Path $output "$_.log"
            if (Test-Path $path) {
                $stream = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
                $reader = [IO.StreamReader]::new($stream)
                try { $reader.ReadToEnd() } finally { $reader.Dispose() }
            } else { '' }
        }
        if (($logs -join "`n").Contains('[lobby-load-test] FAIL')) { throw "Fixture failed: $output" }
        if ($logs[0].Contains('[lobby-load-test] complete side=host') -and $logs[1].Contains('[lobby-load-test] complete side=client')) {
            'PASS' | Set-Content (Join-Path $output 'result.txt')
            "PASS: $Scenario; $output"
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Fixture timeout: $output"
} finally {
    foreach ($process in $launched) {
        if (!$process.HasExited) { Stop-Process -InputObject $process; $process.WaitForExit(5000) | Out-Null }
    }
    $env:CT_ENDING_TEST_SAVE = $previousSave
    $env:CT_ENDING_TEST_SCENARIO = $previousScenario
}
