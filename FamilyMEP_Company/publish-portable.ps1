param(
    [string]$DestinationParent = 'F:\FamilyMEPReleases'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$resolvedParent = [System.IO.Path]::GetFullPath($DestinationParent)
if (-not $resolvedParent.StartsWith('F:\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Portable releases must be created on drive F:.'
}

$builtInSource = Join-Path $projectRoot 'familymep-data\tool-library\built-in'
$libraries = @(Get-ChildItem -LiteralPath $builtInSource -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name.EndsWith('.familymeplib', [System.StringComparison]::OrdinalIgnoreCase) -or $_.Name.EndsWith('.lnlibrary', [System.StringComparison]::OrdinalIgnoreCase) })
if ($libraries.Count -eq 0) {
    throw "No Built-in Library exists. In FamilyMEP use Packages > Freeze as Built-in Library first."
}

& (Join-Path $projectRoot 'build-plugin.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$releaseRoot = Join-Path $resolvedParent ("FamilyMEP_" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
$deployTarget = Join-Path $releaseRoot 'deploy'
$pluginTarget = Join-Path $releaseRoot 'plugin-output'
$dataTarget = Join-Path $releaseRoot 'Data\tool-library\built-in'
New-Item -ItemType Directory -Path $deployTarget -Force | Out-Null
New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
New-Item -ItemType Directory -Path $dataTarget -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $releaseRoot 'shadow') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $releaseRoot 'logs') -Force | Out-Null

Get-ChildItem -LiteralPath (Join-Path $projectRoot 'deploy') -File |
    Where-Object { $_.Name -like 'FamilyMEP.Loader.*' -or $_.Name -like 'RevitHotReload.Abstractions.*' } |
    Copy-Item -Destination $deployTarget -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'plugin-output\FamilyMEP.Plugin.dll') -Destination $pluginTarget -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'plugin-output\FamilyMEP.Plugin.pdb') -Destination $pluginTarget -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath (Join-Path $projectRoot 'plugin-output\FamilyMEP.Plugin.deps.json') -Destination $pluginTarget -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath (Join-Path $projectRoot 'plugin-output\Ui') -Destination $pluginTarget -Recurse -Force
foreach ($library in $libraries) {
    Copy-Item -LiteralPath $library.FullName -Destination (Join-Path $dataTarget $library.Name) -Force
}

$hotloader = [ordered]@{
    PluginAssemblyPath = '..\plugin-output\FamilyMEP.Plugin.dll'
    PluginTypeName = 'FamilyMEP.Plugin.ValveBuilderPlugin'
    ShadowRoot = '..\shadow'
    LogFile = '..\logs\hotloader.log'
}
$hotloader | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $deployTarget 'hotloader.json') -Encoding UTF8

Copy-Item -LiteralPath (Join-Path $projectRoot 'portable-register-current.ps1') -Destination $releaseRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'portable-unregister-current.ps1') -Destination $releaseRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'INSTALL.cmd') -Destination $releaseRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'UNINSTALL.cmd') -Destination $releaseRoot -Force

$libraryBytes = ($libraries | Measure-Object -Property Length -Sum).Sum
Write-Host "Portable release created: $releaseRoot"
Write-Host "Built-in libraries: $($libraries.Count)"
Write-Host ("Compressed library size: {0:N2} GB" -f ($libraryBytes / 1GB))
Write-Host 'Copy this entire folder to drive F: on another computer, then run INSTALL.cmd.'
