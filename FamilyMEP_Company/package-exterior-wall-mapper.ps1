param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'packages')
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$packageName = "ExteriorWallMapper-Revit2025-$stamp"
$stage = Join-Path $OutputDirectory $packageName
$zip = "$stage.zip"

function Copy-FilteredTree {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Destination
    )
    $sourceRoot = [System.IO.Path]::GetFullPath($Source).TrimEnd('\')
    Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|\.vs|\.git)[\\/]'
    } | ForEach-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length).TrimStart('\')
        $target = Join-Path $Destination $relative
        $targetFolder = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $targetFolder)) {
            New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

& (Join-Path $projectRoot 'build-all.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Do not load RevitAPI.dll in a standalone smoke-test process. Autodesk's native
# runtime is hosted by Revit and can raise an application-error dialog while a
# console test process is shutting down, even after the XLSX assertions pass.
# Workbook export is validated during development; packaging only consumes the
# already successful Revit 2025 build above.

New-Item -ItemType Directory -Path $stage -Force | Out-Null
$payload = Join-Path $stage 'Payload'
$runtimeDeploy = Join-Path $payload 'deploy'
$runtimePlugin = Join-Path $payload 'plugin-output'
$sourceTarget = Join-Path $stage 'Source'
New-Item -ItemType Directory -Path $runtimeDeploy -Force | Out-Null
New-Item -ItemType Directory -Path $sourceTarget -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'plugin-output') -Destination $runtimePlugin -Recurse -Force

$deployFiles = @(
    'FamilyMEP.Loader.dll',
    'FamilyMEP.Loader.deps.json',
    'FamilyMEP.Loader.pdb',
    'RevitHotReload.Abstractions.dll',
    'RevitHotReload.Abstractions.deps.json',
    'RevitHotReload.Abstractions.pdb'
)
foreach ($name in $deployFiles) {
    $source = Join-Path $projectRoot "deploy\$name"
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $runtimeDeploy $name) -Force
    }
}

$hotloader = [ordered]@{
    PluginAssemblyPath = '..\plugin-output\FamilyMEP.Plugin.dll'
    PluginTypeName = 'FamilyMEP.Plugin.ValveBuilderPlugin'
    ShadowRoot = '..\shadow'
    LogFile = '..\logs\hotloader.log'
}
$hotloader | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtimeDeploy 'hotloader.json') -Encoding UTF8
New-Item -ItemType Directory -Path (Join-Path $payload 'shadow') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $payload 'logs') -Force | Out-Null

$installScript = @'
$ErrorActionPreference = 'Stop'
$payloadRoot = Join-Path $PSScriptRoot 'Payload'
$payloadLoader = Join-Path $payloadRoot 'deploy\FamilyMEP.Loader.dll'
if (-not (Test-Path -LiteralPath $payloadLoader -PathType Leaf)) {
    throw "Installer payload is incomplete: $payloadLoader"
}

Add-Type -AssemblyName System.Windows.Forms
$defaultParent = Join-Path $env:LOCALAPPDATA 'FamilyMEP'
New-Item -ItemType Directory -Path $defaultParent -Force | Out-Null
$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
$dialog.Description = 'Choose the parent folder for Exterior Wall Mapper 2025. The installer will create an ExteriorWallMapper2025 subfolder.'
$dialog.SelectedPath = $defaultParent
$dialog.ShowNewFolderButton = $true
if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
    Write-Host 'Installation cancelled by the user.'
    exit 2
}

$installRoot = Join-Path ([System.IO.Path]::GetFullPath($dialog.SelectedPath)) 'ExteriorWallMapper2025'
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Get-ChildItem -LiteralPath $payloadRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $installRoot -Recurse -Force
}

$loader = Join-Path $installRoot 'deploy\FamilyMEP.Loader.dll'
if (-not (Test-Path -LiteralPath $loader -PathType Leaf)) { throw "Loader was not installed: $loader" }
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
    <VendorDescription>FamilyMEP Exterior Wall Mapper</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
Set-Content -LiteralPath $target -Value $manifest -Encoding UTF8
Set-Content -LiteralPath (Join-Path $installRoot 'INSTALL_LOCATION.txt') -Value $installRoot -Encoding UTF8
Write-Host "Application installed to: $installRoot"
Write-Host "Revit manifest installed to: $target"
Write-Host 'Restart Revit 2025, then open FamilyMEP > Building Analysis > Exterior Wall Mapper.'
'@
Set-Content -LiteralPath (Join-Path $stage 'install-current-user.ps1') -Value $installScript -Encoding UTF8
Set-Content -LiteralPath (Join-Path $stage 'INSTALL.cmd') -Value "@echo off`r`npowershell.exe -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File `"%~dp0install-current-user.ps1`"`r`npause" -Encoding ASCII

$uninstallScript = @'
$target = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025\FamilyMEP2025.addin'
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Force
    Write-Host "Removed: $target"
} else {
    Write-Host 'FamilyMEP2025.addin was not registered for this user.'
}
'@
Set-Content -LiteralPath (Join-Path $stage 'uninstall-current-user.ps1') -Value $uninstallScript -Encoding UTF8
Set-Content -LiteralPath (Join-Path $stage 'UNINSTALL.cmd') -Value "@echo off`r`npowershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File `"%~dp0uninstall-current-user.ps1`"`r`npause" -Encoding ASCII

$sourceDirectories = @(
    'FamilyMEP.Plugin',
    'RevitHotReload.Abstractions',
    'RevitHotLoader2025',
    'Shared',
    'Assets',
    'tests\ExteriorWallMapperSmokeTest'
)
foreach ($relative in $sourceDirectories) {
    Copy-FilteredTree `
        -Source (Join-Path $projectRoot $relative) `
        -Destination (Join-Path $sourceTarget $relative)
}
$sourceFiles = @(
    'build-all.cmd', 'build-all.ps1', 'build-plugin.cmd', 'build-plugin.ps1',
    'hotloader.json', 'RevitHotLoader2025.addin.template',
    'EXTERIOR_WALL_MAPPER_TEST_VI.md', 'EXTERIOR_WALL_MAPPER_LINEAR_GUIDE_VI.md',
    'package-exterior-wall-mapper.ps1'
)
foreach ($name in $sourceFiles) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination (Join-Path $sourceTarget $name) -Force
}

$standaloneBuild = @'
$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$outputRoot = Join-Path $sourceRoot 'output'
$loaderOutput = Join-Path $outputRoot 'deploy'
$pluginOutput = Join-Path $outputRoot 'plugin-output'
& $dotnet restore (Join-Path $sourceRoot 'RevitHotLoader2025\RevitHotLoader2025.csproj')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet restore (Join-Path $sourceRoot 'FamilyMEP.Plugin\FamilyMEP.Plugin.csproj')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet build (Join-Path $sourceRoot 'RevitHotLoader2025\RevitHotLoader2025.csproj') --configuration Release --output $loaderOutput --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet build (Join-Path $sourceRoot 'FamilyMEP.Plugin\FamilyMEP.Plugin.csproj') --configuration Debug --output $pluginOutput --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Loader output: $loaderOutput"
Write-Host "Plugin output: $pluginOutput"
'@
Set-Content -LiteralPath (Join-Path $sourceTarget 'build-standalone.ps1') -Value $standaloneBuild -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $projectRoot 'EXTERIOR_WALL_MAPPER_LINEAR_GUIDE_VI.md') `
    -Destination (Join-Path $stage 'LINEAR_WORKFLOW_VI.md') -Force

$legacyReadme = @"
# Exterior Wall Mapper — Revit 2025

Package created: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')

## Chạy ngay

1. Giải nén toàn bộ ZIP vào một thư mục cố định.
2. Đóng Revit.
3. Chạy `Runtime\INSTALL.cmd`.
4. Mở lại Revit 2025.
5. Mở `FamilyMEP > Building Analysis > Exterior Wall Mapper`.

Không di chuyển hoặc xóa thư mục Runtime sau khi cài vì manifest trỏ tới loader trong thư mục này.

## Source

Source chính của tool:

- `Source\FamilyMEP.Plugin\ExteriorWallMapperPlugin.cs`
- `Source\FamilyMEP.Plugin\ExteriorWallMapper\`
- `Source\FamilyMEP.Plugin\Ui\ExteriorWallMapperWindow.xaml`

Source có kèm loader, abstraction, assets và smoke test. Yêu cầu Autodesk Revit 2025 và .NET 8 SDK để build.

## Gỡ cài đặt

Đóng Revit rồi chạy `Runtime\UNINSTALL.cmd`.

## Trạng thái bản bàn giao

- Scan MEP Spaces và nested architectural/IFC links.
- Group theo Level và Space Number/Name.
- Export Excel và Replacement Batches.
- Highlight bằng linked selection; nested IFC dùng temporary cyan overlays.
- Exterior rule dùng boundary clusters, bridge gap 600 mm và enclosed gap 15 m.
- LINEAR mapping hiện là kế hoạch mapping/export; chưa ghi trực tiếp vào dữ liệu nội bộ LINEAR.
"@
$readme = @'
# Exterior Wall Mapper — Revit 2025

Package created: {PACKAGE_CREATED}

## Cài đặt

1. Giải nén toàn bộ ZIP.
2. Đóng Autodesk Revit.
3. Chạy `INSTALL.cmd`.
4. Chọn thư mục cha trong hộp thoại. Tool sẽ được cài vào thư mục con `ExteriorWallMapper2025`.
5. Mở lại Revit 2025.
6. Mở `FamilyMEP > Building Analysis > Exterior Wall Mapper`.

Không di chuyển hoặc xóa thư mục cài đặt sau khi đăng ký vì manifest Revit trỏ tới loader trong thư mục này.

Quy trình associate và replace hàng loạt trong LINEAR nằm tại `LINEAR_WORKFLOW_VI.md`.

## Source

Source chính của tool:

- `Source\FamilyMEP.Plugin\ExteriorWallMapperPlugin.cs`
- `Source\FamilyMEP.Plugin\ExteriorWallMapper\`
- `Source\FamilyMEP.Plugin\Ui\ExteriorWallMapperWindow.xaml`

Source có kèm loader, abstraction, assets và smoke test. Yêu cầu Autodesk Revit 2025 và .NET 8 SDK để build.

## Gỡ cài đặt

Đóng Revit rồi chạy `UNINSTALL.cmd`. Sau khi unregister, có thể xóa thư mục cài đặt đã chọn.

## Chức năng

- Scan MEP Spaces và nested architectural/IFC links.
- Group theo Level và Space Number/Name.
- Lập LINEAR Batch Plan cho EWA, EWI và IWA.
- Export Excel và kế hoạch Find & Replace.
- LINEAR mapping là kế hoạch mapping/export; tool không ghi trực tiếp vào dữ liệu nội bộ của LINEAR.
'@
$readme = $readme.Replace('{PACKAGE_CREATED}', (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Set-Content -LiteralPath (Join-Path $stage 'README_VI.md') -Value $readme -Encoding UTF8

Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
$manifest = [ordered]@{
    Package = $packageName
    Created = (Get-Date).ToString('o')
    Zip = $zip
    SizeBytes = (Get-Item -LiteralPath $zip).Length
    SHA256 = $hash.Hash
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath "$zip.sha256.json" -Encoding UTF8
Write-Host "Package folder: $stage"
Write-Host "ZIP: $zip"
Write-Host "SHA256: $($hash.Hash)"
