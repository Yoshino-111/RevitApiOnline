$ErrorActionPreference = 'Stop'
$targetDirectory = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$targets = @(
    (Join-Path $targetDirectory 'FamilyMEP2025.addin'),
    (Join-Path $targetDirectory 'LNFamilyManager2025.addin')
)
$removed = 0
foreach ($target in $targets) {
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        Remove-Item -LiteralPath $target -Force
        Write-Host "Removed: $target"
        $removed++
    }
}
if ($removed -eq 0) {
    Write-Host 'FamilyMEP registration was not found.'
}
