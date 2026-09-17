[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateRange(2020,2027)][int]$Year, [switch]$Unregister)
$ErrorActionPreference = 'Stop'
$bin = Join-Path $PSScriptRoot "Bin\$Year"
$assembly = Join-Path $bin 'FamilyMEP.Entry.dll'
$addinFolder = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Autodesk\Revit\Addins\$Year"
$manifest = Join-Path $addinFolder 'FamilyMEP.MultiVersionTest.addin'
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit before changing test registration.' }
if ($Unregister) {
    if (Test-Path $manifest) {
        [xml]$existing = Get-Content -LiteralPath $manifest
        if ($existing.RevitAddIns.AddIn.Assembly -ne $assembly) { throw 'This manifest belongs to a different test package.' }
        Remove-Item -LiteralPath $manifest
    }
    Write-Host "Removed test registration for Revit $Year."
    exit
}
if (!(Test-Path $assembly)) { throw "Test assembly not found: $assembly" }
$machineAddins = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "Autodesk\Revit\Addins\$Year"
foreach ($folder in @($addinFolder,$machineAddins)) {
    if (!(Test-Path $folder)) { continue }
    foreach ($file in Get-ChildItem $folder -Filter *.addin -File) {
        if ($file.FullName -eq $manifest) { continue }
        if (Select-String -LiteralPath $file.FullName -Pattern 'FamilyMEP|RevitHotLoader|ExteriorWallMapper' -Quiet) {
            throw "Existing FamilyMEP registration detected: $($file.FullName). Disable it for this year before registering the test package."
        }
    }
}
if (Test-Path $manifest) {
    [xml]$existing = Get-Content -LiteralPath $manifest
    if ($existing.RevitAddIns.AddIn.Assembly -ne $assembly) { throw 'Another test package is already registered. Unregister that package first.' }
}
New-Item -ItemType Directory -Force $addinFolder | Out-Null
$escaped = [System.Security.SecurityElement]::Escape($assembly)
$content = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns><AddIn Type="Application">
<Name>FamilyMEP MultiVersion Test</Name>
<Assembly>$escaped</Assembly>
<AddInId>43F6B81E-BB45-4B9C-B79F-90CF744A$Year</AddInId>
<FullClassName>FamilyMEP.Entry.Application</FullClassName>
<VendorId>FamilyMEP</VendorId>
<VendorDescription>FamilyMEP Revit $Year test build</VendorDescription>
</AddIn></RevitAddIns>
"@
[IO.File]::WriteAllText($manifest,$content,[Text.UTF8Encoding]::new($false))
Write-Host "Registered Revit $Year. Open Revit and test the FamilyMEP tab."
