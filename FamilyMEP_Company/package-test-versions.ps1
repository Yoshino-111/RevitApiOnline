[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$portable = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$build = Join-Path $PSScriptRoot 'multi-release'
$package = Join-Path $PSScriptRoot 'packages\FamilyMEP-Revit2020-2027-Test'
if (Test-Path $package) { throw "Package already exists; choose a new folder before rebuilding: $package" }
New-Item -ItemType Directory -Force $package | Out-Null
$verification = Join-Path $package 'Verification'
New-Item -ItemType Directory -Force $verification | Out-Null
$renderer = Join-Path $build 'Tools\PdfRenderer'
$pdfium = Join-Path $portable 'python312\Lib\site-packages\pypdfium2_raw\pdfium.dll'
if (!(Test-Path (Join-Path $renderer 'FamilyMEP.PdfRenderer.exe')) -or !(Test-Path $pdfium)) { throw 'Build the PDF renderer and provide PDFium before packaging.' }
foreach ($year in 2020..2027) {
    $source = Join-Path $build $year
    foreach ($project in @('FamilyMEP.Plugin','FamilyMEP.Release')) {
        $log = Join-Path $build "build-$year-$project.log"
        if (!(Test-Path $log) -or !(Select-String -Path $log -SimpleMatch '0 Error(s)' -Quiet)) { throw "No successful build log: $log" }
        Copy-Item $log $verification
    }
    $bin = Join-Path $package "Bin\$year"
    New-Item -ItemType Directory -Force $bin | Out-Null
    # Ship application dependencies only. Revit's assemblies belong to its host.
    Get-ChildItem $source -File | Where-Object {
        $_.Name -match '^(FamilyMEP\.(Entry|Plugin)|RevitHotReload\.Abstractions|Microsoft\.(Bcl\.|Web\.)|System\.).*\.(dll|json)$'
    } | Copy-Item -Destination $bin
    foreach ($folder in @('Ui','Assets','runtimes')) {
        $inputFolder = Join-Path $source $folder
        if (Test-Path $inputFolder) { Copy-Item $inputFolder -Destination $bin -Recurse }
    }
    $tools = Join-Path $bin 'Tools'
    New-Item -ItemType Directory -Force $tools | Out-Null
    Copy-Item $renderer -Destination $tools -Recurse
    Copy-Item $pdfium -Destination (Join-Path $tools 'PdfRenderer\pdfium.dll')
    foreach ($required in @('FamilyMEP.Entry.dll','FamilyMEP.Plugin.dll','RevitHotReload.Abstractions.dll','Tools\PdfRenderer\FamilyMEP.PdfRenderer.exe','Tools\PdfRenderer\pdfium.dll')) {
        if (!(Test-Path (Join-Path $bin $required))) { throw "Missing package file: $year/$required" }
    }
    $command = "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0register-test-version.ps1`" -Year $year`r`npause`r`n"
    [IO.File]::WriteAllText((Join-Path $package "REGISTER-Revit$year.cmd"), $command, [Text.Encoding]::ASCII)
}
Copy-Item (Join-Path $PSScriptRoot 'register-test-version.ps1') $package
Copy-Item (Join-Path $PSScriptRoot 'MULTIVERSION_TEST_VI.md') (Join-Path $package 'README_VI.md')
2020..2027 | ForEach-Object {
    [pscustomobject]@{ Revit=$_; Compile='PASS'; Ribbon='NOT TESTED'; FamilyCreator='NOT TESTED'; DrainConnection='NOT TESTED'; Sprinkler='NOT TESTED'; ExteriorWallMapper='NOT TESTED'; SmartTag='NOT TESTED'; Notes='' }
} | Export-Csv (Join-Path $package 'TEST_RESULTS.csv') -NoTypeInformation -Encoding UTF8
$records = Get-ChildItem (Join-Path $package 'Bin') -Recurse -File | ForEach-Object {
    [pscustomobject]@{ Path=$_.FullName.Substring($package.Length+1); SHA256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash }
}
$records | Export-Csv (Join-Path $package 'SHA256.csv') -NoTypeInformation -Encoding UTF8
Write-Host "Test package: $package"
