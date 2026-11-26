param([ValidateSet('ShipFullgame','PlaytestFullgame')][string]$Configuration = 'ShipFullgame')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Add-Type -Path "$PSHOME\Microsoft.CodeAnalysis.dll"
Add-Type -Path "$PSHOME\Microsoft.CodeAnalysis.CSharp.dll"

$sources = @(
    (Join-Path $repo 'ScavPrototypeMP-AutoUpdaterPatcher\TogetherAutoUpdaterPatcher.cs'),
    (Join-Path $repo 'ScavPrototypeMP-AutoUpdaterPatcher\StagedPackageApplier.cs')
)
$output = Join-Path $repo "build\autoupdate-patcher-$Configuration"
New-Item -ItemType Directory -Force $output | Out-Null
$game = 'F:\cu-port\CU_Fullgame'
$managed = Join-Path $game 'CasualtiesUnknown_Data\Managed'
$core = Join-Path $game 'BepInEx\core'

$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($path in @(
    (Join-Path $managed 'mscorlib.dll'),
    (Join-Path $managed 'System.dll'),
    (Join-Path $managed 'System.Core.dll'),
    (Join-Path $managed 'System.IO.Compression.dll'),
    (Join-Path $managed 'System.IO.Compression.FileSystem.dll'),
    (Join-Path $core 'BepInEx.dll'),
    (Join-Path $core 'Mono.Cecil.dll')
)) { $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path)) }

$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary
).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion(
    [Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Latest
)
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($source in $sources) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
        [IO.File]::ReadAllText($source),
        $parseOptions
    )
    $trees.Add($tree)
}
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('TogetherAutoUpdaterPatcher', $trees, $refs, $options)
$dll = Join-Path $output 'TogetherAutoUpdaterPatcher.dll'
$stream = [IO.File]::Create($dll)
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
$errors = @($result.Diagnostics | Where-Object Severity -eq 'Error' | ForEach-Object ToString)
[IO.File]::WriteAllLines((Join-Path $output 'errors.log'), [string[]]$errors)
if (!$result.Success) { $errors | Select-Object -First 12; throw "Patcher compile failed; see errors.log" }
"$Configuration patcher compile: 0 errors; $dll"
