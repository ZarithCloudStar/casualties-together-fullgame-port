param([int]$Port=7887, [string]$OldClientDll)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=Join-Path $repo ('build\world-checkpoint-tests\fix7-old-client-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force (Join-Path $output 'save') | Out-Null
$previous=$env:CT_ENDING_TEST_SAVE
$launched=@()
try {
    $env:CT_ENDING_TEST_SAVE=Join-Path $output 'save'
    foreach($side in @('A','B')) {
        $loader=Join-Path $output "runtime-$side"
        $plugins=Join-Path $loader 'plugins\Together'
        New-Item -ItemType Directory -Force $plugins | Out-Null
        $source='F:\cu-port\CU_Fullgame\BepInEx'
        foreach($part in @('core','config','patchers')) { Copy-Item -LiteralPath (Join-Path $source $part) -Destination $loader -Recurse }
        Get-ChildItem (Join-Path $source 'plugins\Together') -File | Where-Object Name -NE 'CasualtiesMP.dll' | Copy-Item -Destination $plugins
        if($side -eq 'A') { Copy-Item -LiteralPath (Join-Path $repo 'build\bugfix-PlaytestFullgame\CasualtiesMP.dll') -Destination $plugins }
        else {
            if (!$OldClientDll) { $OldClientDll = Join-Path $repo 'build\world-checkpoint-tests\fix6-final-original-save-20261112-040129\runtime-B\plugins\Together\CasualtiesMP.dll' }
            Copy-Item -LiteralPath $OldClientDll -Destination $plugins
        }
        $game=Join-Path $repo "build\ending-tests\release19-verified-final-runtime\game-$side\CT_EndingTest_$side.exe"
        $role=if($side -eq 'A') {'--mp-starthost --mp-setname VersionHost --mp-setcharacter 0'} else {'--mp-startclient --mp-setname OldVersionGuest'}
        $args="--mp-nosteam --mp-ending-test --mp-world-checkpoint-test --mp-charsel-timeout 0 --mp-ip-port 127.0.0.1:$Port $role -screen-fullscreen 0 --doorstop-target-assembly `"$loader\core\BepInEx.Preloader.dll`" -logFile `"$output\$side.log`""
        $launched+=Start-Process -FilePath $game -WorkingDirectory (Split-Path $game -Parent) -WindowStyle Hidden -ArgumentList $args -PassThru
    }
    "Started old client rejection: $output"
    $deadline=[DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $p=Join-Path $output 'B.log'
        if(!(Test-Path $p)) {continue}
        $stream=[IO.File]::Open($p,'Open','Read','ReadWrite'); $reader=[IO.StreamReader]::new($stream)
        try {$log=$reader.ReadToEnd()} finally {$reader.Dispose()}
        if($log.Contains('both peers require fix7')) {
            'PASS: actual old client rejected before world entry; explicit fix7/WS1-SC1 version message' | Set-Content (Join-Path $output 'result.txt')
            "PASS: $output"; return
        }
    } while([DateTime]::UtcNow -lt $deadline)
    throw "Old-client rejection not observed: $output"
} finally {
    foreach($process in $launched) {if(!$process.HasExited) {Stop-Process -InputObject $process; $process.WaitForExit(5000) | Out-Null}}
    $env:CT_ENDING_TEST_SAVE=$previous
}
