param(
    [string]$DeployFolder = 'deploy'
)

$ErrorActionPreference = 'Stop'

$releaseRoot = $PSScriptRoot
$loader = Join-Path $releaseRoot "$DeployFolder\FamilyMEP.Loader.dll"
if (-not (Test-Path -LiteralPath $loader -PathType Leaf)) {
    throw "Loader not found: $loader"
}
if (-not [System.IO.Path]::GetFullPath($releaseRoot).StartsWith('F:\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'FamilyMEP portable release must be stored on drive F:.'
}

$targetDirectory = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$target = Join-Path $targetDirectory 'FamilyMEP2025.addin'
$escapedLoader = [System.Security.SecurityElement]::Escape($loader)
$manifest = @"
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>FamilyMEP 2025</Name>
    <Assembly>$escapedLoader</Assembly>
    <AddInId>7882C65E-E2BF-4639-B8CB-6260A07C93B3</AddInId>
    <FullClassName>RevitHotLoader2025.Application</FullClassName>
    <VendorId>FMEP</VendorId>
    <VendorDescription>FamilyMEP</VendorDescription>
  </AddIn>
</RevitAddIns>
"@

New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
Set-Content -LiteralPath $target -Value $manifest -Encoding UTF8
Write-Host "Installed FamilyMEP for the current Windows user."
Write-Host "Add-in manifest: $target"
Write-Host "Tool and family data remain on drive F: $releaseRoot"
Write-Host 'Restart Revit once to load the add-in.'
