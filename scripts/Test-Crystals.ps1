param([ValidatePattern('^[a-z0-9_-]+$')][string]$Phase = 'run1', [switch]$Visual)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repoRoot "build\crystal-tests\$Phase"
New-Item -ItemType Directory -Force $output | Out-Null
$names = @('server.done','client.done','client-heal.done')
foreach ($number in 1..5) { $names += "stage-$number.json"; $names += "stage-$number.json.ready" }
foreach ($name in $names) {
    $file = Join-Path $output $name
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
}
foreach ($copy in @('F:\cu-port\CU_Fullgame','F:\cu-port\CU_Fullgame_B')) {
    $destination = Join-Path $copy 'BepInEx\plugins\Together\CasualtiesMP.dll'
    $label = if ($copy.EndsWith('_B')) { 'B' } else { 'A' }
    $backup = Join-Path $repoRoot "build\crystal-tests\originals\$label-CasualtiesMP.dll"
    if (!(Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $destination -Destination $backup }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'build\crystal-playtest\CasualtiesMP.dll') -Destination $destination -Force
}
$env:CT_CRYSTAL_TEST_DIR = $output
$env:CT_ENDING_TEST_SAVE = Join-Path $output 'save'
try {
    $common = '--mp-nosteam --mp-ip-port 127.0.0.1:7790 --mp-ending-test --mp-crystal-test -screen-fullscreen 0 -screen-width 800 -screen-height 600'
    $hostArgs = "$common --mp-starthost --mp-setname CrystalHost --mp-setcharacter 0 --mp-immidiate-start -logFile $output\host.log"
    $clientArgs = "$common --mp-startclient --mp-setname CrystalClient --mp-setcharacter 1 -logFile $output\client.log"
    $windowStyle = if ($Visual) { 'Normal' } else { 'Hidden' }
    $hostProcess = Start-Process -FilePath 'F:\cu-port\CU_Fullgame\CasualtiesUnknown.exe' -WorkingDirectory 'F:\cu-port\CU_Fullgame' -WindowStyle $windowStyle -ArgumentList $hostArgs -PassThru
    $clientProcess = Start-Process -FilePath 'F:\cu-port\CU_Fullgame_B\CasualtiesUnknown.exe' -WorkingDirectory 'F:\cu-port\CU_Fullgame_B' -WindowStyle $windowStyle -ArgumentList $clientArgs -PassThru
    "$($hostProcess.Id),$($clientProcess.Id)" | Set-Content (Join-Path $output 'pids.txt')
    Get-Content (Join-Path $output 'pids.txt')
} finally { Remove-Item Env:\CT_CRYSTAL_TEST_DIR,Env:\CT_ENDING_TEST_SAVE }
