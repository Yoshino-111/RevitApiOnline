[CmdletBinding()]
param([ValidateRange(2020,2027)][int[]]$Years = @(2020..2027))
$ErrorActionPreference = 'Stop'
$portable = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$output = Join-Path $PSScriptRoot 'multi-release'
$env:DOTNET_CLI_HOME = Join-Path $portable 'cache\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $portable 'cache\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$config = Join-Path $portable 'NuGet.Config'
New-Item -ItemType Directory -Force $output | Out-Null
foreach ($year in $Years) {
    $sdk = if ($year -eq 2027) { 'dotnet10' } else { 'dotnet8' }
    $dotnet = Join-Path $portable "$sdk\dotnet.exe"
    $bin = Join-Path $output $year
    foreach ($name in @('FamilyMEP.Plugin', 'FamilyMEP.Release')) {
        $project = Join-Path $PSScriptRoot "$name\$name.csproj"
        $log = Join-Path $output "build-$year-$name.log"
        Write-Host "Building $name for Revit $year"
        & $dotnet build $project -c Release "-p:RevitVersion=$year" "-p:DependencyFolder=$bin" --configfile $config -o $bin --verbosity quiet *> $log
        if ($LASTEXITCODE -ne 0) {
            Get-Content $log | Where-Object { $_ -match ': error |Error\(s\)' } | Select-Object -First 30
            throw "Build failed: $log"
        }
    }
    foreach ($required in @('FamilyMEP.Plugin.dll','FamilyMEP.Entry.dll','RevitHotReload.Abstractions.dll','Ui\DrainConnectionWindow.xaml','Ui\SprinklerModelerWindow.xaml','Ui\SmartTagWindow.xaml','Ui\ExteriorWallMapperWindow.xaml')) {
        if (!(Test-Path (Join-Path $bin $required))) { throw "Missing output: $year/$required" }
    }
    Write-Host "PASS Revit $year (compile only; in-Revit testing required)"
}
$rendererProject = Join-Path $PSScriptRoot 'FamilyMEP.PdfRenderer\FamilyMEP.PdfRenderer.csproj'
$rendererOutput = Join-Path $output 'Tools\PdfRenderer'
$rendererLog = Join-Path $output 'build-PdfRenderer.log'
& (Join-Path $portable 'dotnet8\dotnet.exe') build $rendererProject -c Release --configfile $config -o $rendererOutput --verbosity quiet *> $rendererLog
if ($LASTEXITCODE -ne 0) { throw "PDF renderer build failed: $rendererLog" }
$pdfium = Join-Path $portable 'python312\Lib\site-packages\pypdfium2_raw\pdfium.dll'
if (!(Test-Path $pdfium)) { throw "PDFium is missing: $pdfium" }
Copy-Item $pdfium (Join-Path $rendererOutput 'pdfium.dll') -Force
Write-Host 'PASS PDF renderer build and native dependency'
