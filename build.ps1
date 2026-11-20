#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManagedDir
)

$ErrorActionPreference = 'Stop'
$managed = (Resolve-Path -LiteralPath $ManagedDir).Path
$assemblies = @(
    'mscorlib.dll'
    'System.dll'
    'System.Core.dll'
    'netstandard.dll'
    'System.Runtime.dll'
    'Assembly-CSharp.dll'
    'Assembly-CSharp-firstpass.dll'
    '0Harmony.dll'
    'UnityEngine.CoreModule.dll'
    'UnityEngine.UIModule.dll'
    'UnityEngine.TextRenderingModule.dll'
    'UnityEngine.UI.dll'
    'Unity.TextMeshPro.dll'
)

[Microsoft.CodeAnalysis.MetadataReference[]] $references = foreach ($file in $assemblies) {
    $path = Join-Path $managed $file
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing game assembly: $path"
    }
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path)
}

[Microsoft.CodeAnalysis.SyntaxTree[]] $sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File |
    ForEach-Object {
        [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
            [IO.File]::ReadAllText($_.FullName),
            [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default,
            $_.Name
        )
    }

$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary
).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithDeterministic($true)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
    'OniResourceBalance', $sources, $references, $options
)
$stream = [IO.MemoryStream]::new()
try {
    $result = $compilation.Emit($stream)
    $result.Diagnostics | Where-Object Severity -ge Warning | ForEach-Object { Write-Host $_.ToString() }
    if (!$result.Success) {
        throw 'Mod compilation failed.'
    }
    $destination = Join-Path $PSScriptRoot 'dist/OniResourceBalance'
    $null = New-Item -ItemType Directory -Path $destination -Force
    [IO.File]::WriteAllBytes((Join-Path $destination 'OniResourceBalance.dll'), $stream.ToArray())
    foreach ($file in 'mod.yaml', 'mod_info.yaml', 'setup.cmd', 'setup.ps1', 'README.md', 'LICENSE', 'NOTICE.md') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $destination
    }
    Get-ChildItem -LiteralPath $destination -File | Select-Object Name, Length, FullName
}
finally {
    $stream.Dispose()
}
