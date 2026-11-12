param([ValidateSet('ShipFullgame','PlaytestFullgame')][string]$Configuration, [switch]$SpectatorBaseline)
# SDK-less fallback. Uses PowerShell's compiler and the repository's existing
# publicizer output/package cache. Does not replace the normal SDK build.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Add-Type -Path "$PSHOME\Microsoft.CodeAnalysis.dll"
Add-Type -Path "$PSHOME\Microsoft.CodeAnalysis.CSharp.dll"
$project = Join-Path $repo 'ScavPrototypeMP'
$obj = Join-Path $repo "build\artifacts\CasualtiesTogether\obj\$Configuration"
$output = Join-Path $repo "build\bugfix-$Configuration"
if ($SpectatorBaseline) { $output = Join-Path $repo "build\spectator-tests\baseline-build-$Configuration" }
New-Item -ItemType Directory -Force $output | Out-Null
$defines = @('CASUALTIESTOGETHER','FULLGAME','RELEASE','MP_GAMEMODES','UNITY_2018_3_OR_NEWER','UNITY_2019_1_OR_NEWER','UNITY_5_3_OR_NEWER','UNITY_2018_4_OR_NEWER')
if ($Configuration -eq 'PlaytestFullgame') { $defines += @('TESTING','PLAYTEST','PRERELEASE') }
$options = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview).WithPreprocessorSymbols([string[]]$defines)
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$sourceFiles = @(Get-ChildItem $project -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '[\\/]obj[\\/]|[\\/]bin[\\/]|Optimizinator[\\/]Evidence[\\/]archived[\\/]EvidenceBag.cs$' })
$sourceFiles += @(Get-Item "$obj\BuildInfo.cs", "$obj\MyPluginInfo.cs", "$obj\CasualtiesTogether.AssemblyInfo.cs", "$obj\CasualtiesTogether.IgnoresAccessChecksTo.cs")
$assets = Get-Content (Join-Path $repo 'build\artifacts\CasualtiesTogether\obj\project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$cache = @($assets.packageFolders.Keys)[0]
$paths = @{}
foreach ($entry in $assets.targets['.NETStandard,Version=v2.1'].GetEnumerator()) {
    $library = Join-Path $cache $assets.libraries[$entry.Key].path
    foreach ($relative in $entry.Value.compile.Keys) {
        if ($relative.EndsWith('.dll') -and (!$entry.Key.StartsWith('System.') -or $entry.Key.StartsWith('System.Runtime.CompilerServices.Unsafe/'))) { $path = Join-Path $library $relative; $paths[[IO.Path]::GetFileName($path)] = $path }
    }
    foreach ($relative in $entry.Value.contentFiles.Keys) {
        if ($entry.Value.contentFiles[$relative].buildAction -eq 'Compile') { $sourceFiles += Get-Item (Join-Path $library $relative) }
    }
}
foreach ($file in $sourceFiles) {
    # Compile the preserved six original spectator/ownership sources without
    # reverting or modifying the working tree or the final DLL output.
    if ($SpectatorBaseline) {
        if ($file.Name -eq 'SpectatorPatches.cs') { continue }
        $original = Join-Path $repo ('build\spectator-tests\original-source\' + $file.Name)
        if (Test-Path -LiteralPath $original) { $file = Get-Item -LiteralPath $original }
    }
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName), $options, $file.FullName))
}
# Unity's framework facade set is used, never PowerShell's .NET runtime refs.
$managed = 'F:\cu-port\CU_Fullgame\CasualtiesUnknown_Data\Managed'
foreach ($file in Get-ChildItem $managed -Recurse -Filter '*.dll' | Where-Object Name -NE 'com.rlabrecque.steamworks.net.dll') { $paths[$file.Name] = $file.FullName }
foreach ($file in Get-ChildItem (Join-Path $repo 'libs\dlls') -Filter '*.dll' | Where-Object Name -NotIn @('opus.dll','steam_api64.dll')) { $paths[$file.Name] = $file.FullName }
foreach ($file in Get-ChildItem 'F:\cu-port\CU_Fullgame\BepInEx\core' -Filter '*.dll' | Where-Object Name -NE '0Harmony20.dll') {
    $paths[$file.Name] = $file.FullName
}
$paths['Assembly-CSharp.dll'] = Join-Path $obj 'publicized\Assembly-CSharp.dll'
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($path in $paths.Values) { $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path)) }
$compileOptions = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithAllowUnsafe($true).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('CasualtiesMP', $trees, $refs, $compileOptions)
$resources = [Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
[xml]$xml = Get-Content (Join-Path $project 'CasualtiesTogether.csproj') -Raw
foreach ($resource in $xml.Project.ItemGroup.EmbeddedResource) {
    if (!$resource.Include) { continue }
    $resourcePath = Join-Path $project $resource.Include
    $name = 'Together.' + $resource.Include.Replace('\','.').Replace('/','.')
    $factory = [Func[IO.Stream]]{ [IO.File]::OpenRead($resourcePath) }.GetNewClosure()
    $resources.Add([Microsoft.CodeAnalysis.ResourceDescription]::new($name, $factory, $true))
}
$stream = [IO.File]::Create((Join-Path $output 'CasualtiesMP.dll'))
try { $result = $compilation.Emit($stream, $null, $null, $null, $resources) }
finally { $stream.Dispose() }
$diagnostics = @($result.Diagnostics | Where-Object Severity -eq 'Error' | ForEach-Object ToString)
[IO.File]::WriteAllLines((Join-Path $output 'errors.log'), [string[]]$diagnostics)
if (!$result.Success) { $diagnostics | Select-Object -First 12; throw "Offline compile failed: $Configuration; see errors.log" }
"$Configuration Roslyn compile: 0 errors; $output (SDK/MSBuild unavailable)"
