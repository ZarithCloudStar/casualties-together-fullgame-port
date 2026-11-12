param([string]$Run = 'fixed', [int]$Port = 7886, [switch]$Baseline, [string]$TestDll, [switch]$SlowGeneration)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$TestDll) { $TestDll = Join-Path $repo 'build\bugfix-PlaytestFullgame\CasualtiesMP.dll' }
$TestDll = (Resolve-Path -LiteralPath $TestDll).Path
$output = Join-Path $repo ("build\medical-sandbox-tests\$Run-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force (Join-Path $output 'save') | Out-Null
$runtime = Join-Path $repo 'build\spectator-tests\runtime-101'
# Never reuse the ending fixture's historical 1.0 runtime for a 1.0.1 test.
# Copy read-only source game files into this repository; all loader/log/save
# writes stay here. Validate the game assembly before each launch.
$gameSource = 'F:\cu-port\CU_Fullgame'
$gameHash = (Get-FileHash (Join-Path $gameSource 'CasualtiesUnknown_Data\Managed\Assembly-CSharp.dll')).Hash
foreach ($side in @('A','B')) {
    $gameFolder = Join-Path $runtime "game-$side"
    $dataFolder = Join-Path $gameFolder "CT_EndingTest_$($side)_Data"
    if (!(Test-Path (Join-Path $dataFolder 'Managed\Assembly-CSharp.dll'))) {
        New-Item -ItemType Directory -Force $gameFolder | Out-Null
        Get-ChildItem -LiteralPath $gameSource -File | Where-Object Name -NE 'CasualtiesUnknown.exe' | Copy-Item -Destination $gameFolder
        Copy-Item -LiteralPath (Join-Path $gameSource 'CasualtiesUnknown.exe') -Destination (Join-Path $gameFolder "CT_EndingTest_$side.exe")
        Copy-Item -LiteralPath (Join-Path $gameSource 'MonoBleedingEdge') -Destination $gameFolder -Recurse
        Copy-Item -LiteralPath (Join-Path $gameSource 'CasualtiesUnknown_Data') -Destination $dataFolder -Recurse
    }
    if ((Get-FileHash (Join-Path $dataFolder 'Managed\Assembly-CSharp.dll')).Hash -ne $gameHash) { throw "Test runtime game assembly mismatch: $gameFolder" }
}
$gameHash | Set-Content (Join-Path $output 'game-assembly-sha256.txt')
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
        Copy-Item -LiteralPath $TestDll -Destination (Join-Path $loader 'plugins\Together\CasualtiesMP.dll')
        $game = Join-Path $runtime "game-$side\CT_EndingTest_$side.exe"
        $role = if ($side -eq 'A') { '--mp-starthost --mp-setname HOST --mp-setcharacter 0' } else { '--mp-startclient --mp-setname FRIEND --mp-setcharacter 1' }
        $common = "--mp-nosteam --mp-ending-test --mp-medical-sandbox-test --mp-charsel-timeout 0 --mp-ip-port 127.0.0.1:$Port -screen-fullscreen 0 -screen-width 800 -screen-height 600"
        if ($Baseline) { $common += ' --mp-diagnosis' }
        if ($SlowGeneration) { $common += ' --mp-delay-sandbox-worldplace' }
        $arguments = "$common $role --doorstop-target-assembly `"$loader\core\BepInEx.Preloader.dll`" -logFile `"$output\$side.log`""
        $launched += Start-Process -FilePath $game -WorkingDirectory (Split-Path $game -Parent) -WindowStyle Hidden -ArgumentList $arguments -PassThru
    }
    $launched.Id | Set-Content (Join-Path $output 'pids.txt')
    Get-FileHash (Join-Path $output 'runtime-A\plugins\Together\CasualtiesMP.dll') | Format-List | Out-File (Join-Path $output 'dll-sha256.txt')
    "Started $Run; logs: $output"
    $deadline = [DateTime]::UtcNow.AddSeconds(240)
    do {
        Start-Sleep -Seconds 2
        if (Test-Path (Join-Path $output 'save\failed')) { throw "Fixture failed: $(Get-Content (Join-Path $output 'save\failed')); $output" }
        if ((Test-Path (Join-Path $output 'save\host.done')) -and (Test-Path (Join-Path $output 'save\client.done'))) {
            "PASS: $output" | Set-Content (Join-Path $output 'result.txt'); "PASS: $output"; return
        }
        if (@($launched | Where-Object { !$_.HasExited }).Count -eq 0) { throw "Both isolated instances exited: $output" }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Fixture timeout: $output"
} finally {
    foreach ($process in $launched) { if (!$process.HasExited) { Stop-Process -InputObject $process; $process.WaitForExit(5000) | Out-Null } }
    $env:CT_ENDING_TEST_SAVE = $previousSave
}

