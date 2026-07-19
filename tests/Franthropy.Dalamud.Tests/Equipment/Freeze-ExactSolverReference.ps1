[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\..\..\src\Franthropy.Dalamud\Equipment\EquipmentExactFrontierSolver.cs'),
    [string]$DestinationPath = (Join-Path $PSScriptRoot 'Reference\EquipmentExactFrontierReferenceSolver.cs'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourcePath)
$destination = [IO.Path]::GetFullPath($DestinationPath)
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Exact solver source was not found: $source"
}
if ((Test-Path -LiteralPath $destination) -and -not $Force) {
    throw "Reference solver already exists: $destination. Delete it deliberately or pass -Force when refreshing the canonical regression witness."
}

$text = [IO.File]::ReadAllText($source)
$declaration = 'public sealed class EquipmentExactFrontierSolver'
$start = $text.IndexOf($declaration, [StringComparison]::Ordinal)
if ($start -lt 0) {
    throw 'Could not locate the exact solver declaration.'
}
$body = $text.Substring($start).Replace(
    $declaration,
    'internal sealed class EquipmentExactFrontierReferenceSolver')
$header = @'
// This test-only witness preserves one frozen canonical sequential traversal and its
// DominatesPartial behavior for regression comparison. It is not a semantic oracle for all
// feasible terminal lineage, whose count and canonical representative are traversal-independent.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment.Reference;

'@
[IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
[IO.File]::WriteAllText($destination, $header + $body)

[pscustomobject]@{
    Source = $source
    Destination = $destination
    Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
}
