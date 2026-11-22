param([ValidatePattern('^[a-z0-9_-]+$')][string]$Phase = 'stage1',
    [ValidateSet('cargo','travel','full-route','full-route-host','quit-lobby','quit-world','merc','escape-success','escape-timeout','escape-down','escape-killed','disconnect','reload','r14-merc','r14-escape','r14-remote','r14-coredeath','r14-capture','r14-timeout','r14-noncoredeath','r14-chipped','r18-ready','r18-layer10','r14-god-assist','r19-noncore-host','r19-noncore-client','r19-core-host','r19-core-client','r19-merc')][string]$Scenario = 'cargo',
    [switch]$Interactive,
    [switch]$IsolatedLoader,
    [switch]$AutoNarrative,
    [string]$TestDll,
    [string]$GameA = 'F:\cu-port\CU_Fullgame\CasualtiesUnknown.exe',
    [string]$GameB = 'F:\cu-port\CU_Fullgame_B\CasualtiesUnknown.exe',
    [int]$Port = 7790)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$copies = @($GameA, $GameB)
if (!$IsolatedLoader -and (Get-Process CasualtiesUnknown -ErrorAction SilentlyContinue | Where-Object Path -In $copies)) { throw 'A/B instances already running; close them before deploying a test DLL, or use -IsolatedLoader.' }
if ([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveUdpListeners() | Where-Object Port -EQ $Port) { throw "UDP port $Port is already in use; choose a free -Port or close that test session first." }
$output = Join-Path $repoRoot "build\ending-tests\$Phase"
New-Item -ItemType Directory -Force $output | Out-Null
if (!$TestDll) { $TestDll = Join-Path $repoRoot 'build\ending-playtest\CasualtiesMP.dll' }
$testDll = (Resolve-Path -LiteralPath $TestDll).Path
if (!$IsolatedLoader) { foreach ($copy in @('F:\cu-port\CU_Fullgame', 'F:\cu-port\CU_Fullgame_B')) {
    $destination = Join-Path $copy 'BepInEx\plugins\Together\CasualtiesMP.dll'
    if ((Get-FileHash -LiteralPath $testDll).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        Copy-Item -LiteralPath $testDll -Destination $destination
    }
} }
$loaders = @()
if ($IsolatedLoader) {
    foreach ($side in @('A','B')) {
        $source = if ($side -eq 'A') { 'F:\cu-port\CU_Fullgame\BepInEx' } else { 'F:\cu-port\CU_Fullgame_B\BepInEx' }
        $runtime = Join-Path $output "runtime-$side"
        New-Item -ItemType Directory -Force (Join-Path $runtime 'plugins\Together') | Out-Null
        foreach ($part in @('core','config','patchers')) {
            if (Test-Path -LiteralPath (Join-Path $source $part)) { Copy-Item -LiteralPath (Join-Path $source $part) -Destination $runtime -Recurse }
        }
        Get-ChildItem -LiteralPath (Join-Path $source 'plugins\Together') -File | Where-Object Name -NE 'CasualtiesMP.dll' | Copy-Item -Destination (Join-Path $runtime 'plugins\Together')
        Copy-Item -LiteralPath $testDll -Destination (Join-Path $runtime 'plugins\Together\CasualtiesMP.dll')
        $loaders += Join-Path $runtime 'core\BepInEx.Preloader.dll'
    }
}
$env:CT_ENDING_TEST_SAVE = Join-Path $output 'save'
$env:CT_ENDING_TEST_SCENARIO = $Scenario
if ($Scenario -eq 'reload') {
    $savedFixture = Join-Path $repoRoot 'build\ending-tests\stage3-travel\save'
    New-Item -ItemType Directory -Force $env:CT_ENDING_TEST_SAVE | Out-Null
    Copy-Item -Path (Join-Path $savedFixture '*') -Destination $env:CT_ENDING_TEST_SAVE -Recurse -Force
}
try {
    $common = "--mp-nosteam --mp-ip-port 127.0.0.1:$Port --mp-ending-test -screen-fullscreen 0 -screen-width 800 -screen-height 600"
    if ($AutoNarrative) { $common += ' --mp-auto-narrative' }
    $hostName = 'EndingHost'
    $clientName = 'EndingClient'
    $hostArgs = "$common --mp-starthost --mp-setname $hostName --mp-setcharacter 0 --mp-immidiate-start -logFile $output\host.log"
    # R14 fixtures own StartRun after both lobby intros settle; the generic
    # immediate-start coroutine can otherwise race the client's first baseline.
    if ($Scenario.StartsWith('r14-') -or $Scenario.StartsWith('r18-') -or $Scenario.StartsWith('r19-') -or $Scenario.StartsWith('escape')) { $hostArgs = $hostArgs.Replace(' --mp-immidiate-start', '') }
    if ($Scenario -eq 'reload') { $hostArgs = "$common --mp-starthost --mp-setname EndingHost --mp-setcharacter 0 -logFile $output\host.log" }
    $clientArgs = "$common --mp-startclient --mp-setname $clientName --mp-setcharacter 1 -logFile $output\client.log"
    if ($IsolatedLoader) {
        $hostArgs += " --doorstop-target-assembly `"$($loaders[0])`""
        $clientArgs += " --doorstop-target-assembly `"$($loaders[1])`""
    }
    $windowStyle = if ($Interactive) { 'Normal' } else { 'Hidden' }
    $hostProcess = Start-Process -FilePath $GameA -WorkingDirectory (Split-Path $GameA -Parent) -WindowStyle $windowStyle -ArgumentList $hostArgs -PassThru
    $clientProcess = Start-Process -FilePath $GameB -WorkingDirectory (Split-Path $GameB -Parent) -WindowStyle $windowStyle -ArgumentList $clientArgs -PassThru
    "$($hostProcess.Id),$($clientProcess.Id)" | Set-Content (Join-Path $output 'pids.txt')
    Get-Content (Join-Path $output 'pids.txt')
} finally {
    Remove-Item Env:\CT_ENDING_TEST_SAVE,Env:\CT_ENDING_TEST_SCENARIO
}
