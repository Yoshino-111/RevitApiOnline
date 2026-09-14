$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'deploy\FamilyMEP2025.addin'
$targetDirectory = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$target = Join-Path $targetDirectory 'FamilyMEP2025.addin'

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Build the loader first: $source was not found."
}

New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $target -Force
$legacyTarget = Join-Path $targetDirectory 'RevitHotLoader2025.addin'
if (Test-Path -LiteralPath $legacyTarget -PathType Leaf) {
    Remove-Item -LiteralPath $legacyTarget -Force
}
Write-Host "Registered for the current Windows user: $target"
Write-Host 'Restart Revit once. Future FamilyMEP DLL rebuilds do not require a Revit restart.'
