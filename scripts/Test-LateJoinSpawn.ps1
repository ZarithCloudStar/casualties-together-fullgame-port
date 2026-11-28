param([string]$Run = 'fixed', [int]$Port = 7893, [switch]$Baseline, [string]$TestDll, [string]$GameSource = 'F:\cu-port\CU_Fullgame', [switch]$Layer10)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$TestDll) { $TestDll = Join-Path $repo 'build\bugfix-PlaytestFullgame\CasualtiesMP.dll' }
$TestDll = (Resolve-Path -LiteralPath $TestDll).Path
$output = Join-Path $repo ("build\latejoin-tests\$Run-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force (Join-Path $output 'save') | Out-Null
$gameHash = (Get-FileHash (Join-Path $gameSource 'CasualtiesUnknown_Data\Managed\Assembly-CSharp.dll')).Hash
$runtime = Join-Path $repo "build\latejoin-tests\runtime-$($gameHash.Substring(0, 12))"
# Keep test writes inside the repository and distinguish different game runtimes.
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
function Start-Instance([string]$side, [string]$phase, [string]$playerName) {
    $loader = Join-Path $output "runtime-$phase"
    $source = 'F:\cu-port\CU_Fullgame\BepInEx'
    New-Item -ItemType Directory -Force (Join-Path $loader 'plugins\Together') | Out-Null
    foreach ($part in @('core','config','patchers')) { Copy-Item -LiteralPath (Join-Path $source $part) -Destination $loader -Recurse }
    Get-ChildItem (Join-Path $source 'plugins\Together') -File | Where-Object Name -NotIn @('CasualtiesMP.dll', 'Steamworks.NET.dll') | Copy-Item -Destination (Join-Path $loader 'plugins\Together')
    Copy-Item -LiteralPath (Join-Path $repo 'libs\dlls\CUCoreLib\CUCoreLib.dll') -Destination (Join-Path $loader 'plugins')
    Copy-Item -LiteralPath $TestDll -Destination (Join-Path $loader 'plugins\Together\CasualtiesMP.dll')
    $game = Join-Path $runtime "game-$side\CT_EndingTest_$side.exe"
    $role = if ($side -eq 'A') { "--mp-starthost --mp-setname $playerName --mp-setcharacter 0" } else { "--mp-startclient --mp-setname $playerName --mp-setcharacter 1" }
    $common = "--mp-nosteam --mp-ending-test --mp-late-spawn-test --mp-late-phase=$phase --mp-charsel-timeout 0 --mp-ip-port 127.0.0.1:$Port -screen-fullscreen 0 -screen-width 800 -screen-height 600"
    if ($Baseline) { $common += ' --mp-diagnosis' }
    if ($Layer10) { $common += ' --mp-layer10-spawn-test' }
    $arguments = "$common $role --doorstop-target-assembly `"$loader\core\BepInEx.Preloader.dll`" -logFile `"$output\$phase.log`""
    Start-Process -FilePath $game -WorkingDirectory (Split-Path $game -Parent) -WindowStyle Hidden -ArgumentList $arguments -PassThru
}
function Wait-Marker([string]$marker) {
    $deadline = [DateTime]::UtcNow.AddSeconds(150)
    while (!(Test-Path (Join-Path $output "save\$marker"))) {
        if (Test-Path (Join-Path $output 'save\failed')) { throw "Fixture failed: $(Get-Content (Join-Path $output 'save\failed')); $output" }
        if ($launched[0].HasExited) { throw "Host exited while waiting for $marker; $output" }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Timeout waiting for $marker; $output" }
        Start-Sleep -Seconds 1
    }
}
try {
    $env:CT_ENDING_TEST_SAVE = Join-Path $output 'save'
    $launched += Start-Instance 'A' 'host' 'HOST'
    $launched += Start-Instance 'B' 'initial' 'FRIEND'
    "Started $Run; logs: $output"
    Get-FileHash $TestDll | Select-Object Path,Hash | ConvertTo-Json | Set-Content (Join-Path $output 'dll-sha256.json')
    Wait-Marker 'initial.done'
    if (!$launched[-1].HasExited) { Stop-Process -InputObject $launched[-1]; $launched[-1].WaitForExit(5000) | Out-Null }
    $phases = @(,@('new','FRIEND1'))
    if (!$Baseline -and !$Layer10) { $phases += ,@('off','FRIEND2'); $phases += ,@('saved','FRIEND') }
    foreach ($item in $phases) {
        $phase = $item[0]; $playerName = $item[1]
        Wait-Marker "join-$phase"
        $launched += Start-Instance 'B' $phase $playerName
        Wait-Marker "$phase.done"
        if (!$launched[-1].HasExited) { Stop-Process -InputObject $launched[-1]; $launched[-1].WaitForExit(5000) | Out-Null }
    }
    Wait-Marker 'host.done'
    "PASS: $output" | Set-Content (Join-Path $output 'result.txt')
    "PASS: $output"
} finally {
    foreach ($process in $launched) { if (!$process.HasExited) { Stop-Process -InputObject $process; $process.WaitForExit(5000) | Out-Null } }
    $env:CT_ENDING_TEST_SAVE = $previousSave
}

