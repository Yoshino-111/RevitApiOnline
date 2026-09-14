param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'packages')
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$packageName = "ExteriorWallMapper_LucDev_R25_$stamp"
$stage = Join-Path $OutputDirectory $packageName
$zip = "$stage.zip"

$files = @(
    'DEV_UPDATE_README_VI.md',
    'build-plugin.ps1',
    'package-exterior-wall-mapper.ps1',
    'package-exterior-wall-mapper-dev.ps1',
    'FamilyMEP.Plugin\FamilyMEP.Plugin.csproj',
    'FamilyMEP.Plugin\ExteriorWallMapperPlugin.cs',
    'FamilyMEP.Plugin\ExteriorWallMapper\ExteriorWallExcelExporter.cs',
    'FamilyMEP.Plugin\ExteriorWallMapper\ExteriorWallMapperController.cs',
    'FamilyMEP.Plugin\ExteriorWallMapper\ExteriorWallModels.cs',
    'FamilyMEP.Plugin\ExteriorWallMapper\ExteriorWallScanner.cs',
    'FamilyMEP.Plugin\Ui\ExteriorWallMapperWindow.xaml',
    'tests\ExteriorWallMapperSmokeTest\ExteriorWallMapperSmokeTest.csproj',
    'tests\ExteriorWallMapperSmokeTest\Program.cs'
)

New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($relativePath in $files) {
    $source = Join-Path $projectRoot $relativePath
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Developer package source file is missing: $source"
    }
    $destination = Join-Path $stage $relativePath
    $destinationFolder = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $destinationFolder)) {
        New-Item -ItemType Directory -Path $destinationFolder -Force | Out-Null
    }
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal -Force
$zipItem = Get-Item -LiteralPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Write-Output "Developer package: $zip"
Write-Output ("Size: {0:N0} bytes ({1:N2} KB)" -f $zipItem.Length, ($zipItem.Length / 1KB))
Write-Output "SHA256: $hash"
