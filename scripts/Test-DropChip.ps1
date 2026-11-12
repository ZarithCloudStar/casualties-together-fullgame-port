param(
    [ValidatePattern('^[a-z0-9_-]+$')][string]$Phase = ('drop-chip-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [string]$DllFolder = 'ending-playtest',
    [string]$Scenario = 'drop-chip',
    [int]$Port = 7791,
    [string]$LoadSave = '',
    [int]$ClientDelaySeconds = 0,
    [switch]$Crystals,
    [switch]$Sandbox,
    [string]$HostName = 'DropHost',
    [string]$ClientName = 'DropClient',
    [switch]$WaitForResult,
    [int]$TimeoutSeconds = 360
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repoRoot "build\drop-chip-tests\$Phase"
if (Test-Path -LiteralPath $output) { throw 'Use a fresh phase directory.' }
New-Item -ItemType Directory -Force $output | Out-Null
$dll = Join-Path $repoRoot "build\$DllFolder\CasualtiesMP.dll"
foreach ($side in @('A','B')) {
    $source = if ($side -eq 'A') { 'F:\cu-port\CU_Fullgame' } else { 'F:\cu-port\CU_Fullgame_B' }
    $target = Join-Path $repoRoot "build\runtime-$side"
    if (!(Test-Path -LiteralPath (Join-Path $target 'CasualtiesUnknown.exe'))) {
        New-Item -ItemType Directory -Force $target | Out-Null
        Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $target
        foreach ($dir in @('CasualtiesUnknown_Data','MonoBleedingEdge')) {
            New-Item -ItemType Junction -Path (Join-Path $target $dir) -Target (Join-Path $source $dir) | Out-Null
        }
        New-Item -ItemType Directory -Force (Join-Path $target 'BepInEx') | Out-Null
        foreach ($dir in @('core','patchers','plugins','config')) {
            Copy-Item -LiteralPath (Join-Path $source "BepInEx\$dir") -Destination (Join-Path $target 'BepInEx') -Recurse
        }
    }
}
foreach ($copy in @((Join-Path $repoRoot 'build\runtime-A'),(Join-Path $repoRoot 'build\runtime-B'))) {
    $destination = Join-Path $copy 'BepInEx\plugins\Together\CasualtiesMP.dll'
    if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $dll).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        Copy-Item -LiteralPath $dll -Destination $destination
    }
}
$env:CT_ENDING_TEST_SAVE = Join-Path $output 'save'
$env:CT_ENDING_TEST_SCENARIO = $Scenario
if ($Crystals) { $env:CT_CRYSTAL_TEST_DIR = $output }
if ($LoadSave) {
    New-Item -ItemType Directory -Force $env:CT_ENDING_TEST_SAVE | Out-Null
    Copy-Item -Path (Join-Path $LoadSave '*') -Destination $env:CT_ENDING_TEST_SAVE -Recurse
    if ($Scenario.EndsWith('legacy')) {
        $choices = Join-Path $env:CT_ENDING_TEST_SAVE 'character_choices.txt'
        $lines = Get-Content -LiteralPath $choices | ForEach-Object {
            $columns = $_.Split('|')
            if ($columns.Count -eq 4) { $columns[0..2] -join '|' } else { $_ }
        }
        $lines | Set-Content -LiteralPath $choices -Encoding utf8
    }
}
try {
    $common = "--mp-nosteam --mp-ip-port 127.0.0.1:$Port --mp-ending-test -screen-fullscreen 0 -screen-width 800 -screen-height 600"
    if ($Crystals) { $common += ' --mp-crystal-test' }
    if ($Sandbox) { $common += ' --mp-sandbox-test' }
    $hostArgs = "$common --mp-starthost --mp-setname $HostName --mp-setcharacter 0 -logFile $output\host.log"
    $clientArgs = "$common --mp-startclient --mp-setname $ClientName -logFile $output\client.log"
    if ($Crystals -or $Scenario.StartsWith('full-route')) { $clientArgs += ' --mp-setcharacter 1' }
    $aDir = Join-Path $repoRoot 'build\runtime-A'
    $bDir = Join-Path $repoRoot 'build\runtime-B'
    $a = Start-Process -FilePath (Join-Path $aDir 'CasualtiesUnknown.exe') -WorkingDirectory $aDir -WindowStyle Hidden -ArgumentList $hostArgs -PassThru
    $a.Id | Set-Content (Join-Path $output 'pids.txt')
    if ($ClientDelaySeconds -gt 0) { Start-Sleep -Seconds $ClientDelaySeconds }
    $b = Start-Process -FilePath (Join-Path $bDir 'CasualtiesUnknown.exe') -WorkingDirectory $bDir -WindowStyle Hidden -ArgumentList $clientArgs -PassThru
    "$($a.Id),$($b.Id)" | Set-Content (Join-Path $output 'pids.txt')
    Write-Output $output
    if ($WaitForResult) {
        $passed = $false
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                $failures = Get-ChildItem -LiteralPath $output -Filter '*.log' | Select-String -Pattern '\[(drop|crystal|route|sandbox)-test\] FAIL'
                if ($failures) { throw ($failures.Line -join "`n") }
                $hostDone = (Test-Path "$output\host.done") -or (Test-Path "$output\server.done")
                $clientDone = Test-Path "$output\client.done"
                if ($Sandbox) {
                    $hostDone = [bool](Select-String -Path "$output\host.log" -Pattern '\[sandbox-test\] complete side=host')
                    $clientDone = [bool](Select-String -Path "$output\client.log" -Pattern '\[sandbox-test\] complete side=client')
                }
                if ($hostDone -and $clientDone) { $passed = $true; break }
                Start-Sleep -Seconds 2
            }
            if (!$passed) { throw "Timed out: $output" }
            Write-Output "PASS $Scenario"
        } finally {
            foreach ($process in @($a,$b)) {
                if (!$process.HasExited -and $process.Path -in @((Join-Path $aDir 'CasualtiesUnknown.exe'),(Join-Path $bDir 'CasualtiesUnknown.exe'))) {
                    Stop-Process -InputObject $process
                    $process.WaitForExit()
                }
            }
            [pscustomobject]@{scenario=$Scenario; passed=$passed; port=$Port; logs=$output} | ConvertTo-Json | Set-Content (Join-Path $output 'result.json')
        }
    }
} finally {
    Remove-Item Env:\CT_ENDING_TEST_SAVE,Env:\CT_ENDING_TEST_SCENARIO
    if ($Crystals) { Remove-Item Env:\CT_CRYSTAL_TEST_DIR }
}
