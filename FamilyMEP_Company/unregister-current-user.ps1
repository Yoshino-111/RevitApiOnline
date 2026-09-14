$ErrorActionPreference = 'Stop'

$targetDirectory = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$target = Join-Path $targetDirectory 'FamilyMEP2025.addin'
$resolvedDirectory = [System.IO.Path]::GetFullPath($targetDirectory).TrimEnd('\') + '\'
$resolvedTarget = [System.IO.Path]::GetFullPath($target)

if (-not $resolvedTarget.StartsWith($resolvedDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to remove a manifest outside the Revit 2025 current-user add-in folder.'
}

if (Test-Path -LiteralPath $resolvedTarget -PathType Leaf) {
    Remove-Item -LiteralPath $resolvedTarget -Force
    Write-Host "Removed: $resolvedTarget"
} else {
    Write-Host 'The current-user manifest is not installed.'
}

$legacyTarget = Join-Path $targetDirectory 'RevitHotLoader2025.addin'
if (Test-Path -LiteralPath $legacyTarget -PathType Leaf) {
    Remove-Item -LiteralPath $legacyTarget -Force
    Write-Host "Removed legacy manifest: $legacyTarget"
}
