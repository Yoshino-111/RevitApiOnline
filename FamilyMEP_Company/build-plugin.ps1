$portableRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
. (Join-Path $portableRoot 'activate.ps1')

$dotnet = Join-Path $portableRoot 'dotnet8\dotnet.exe'
$nugetConfig = Join-Path $portableRoot 'NuGet.Config'
$pluginProject = Join-Path $PSScriptRoot 'FamilyMEP.Plugin\FamilyMEP.Plugin.csproj'
$rendererProject = Join-Path $PSScriptRoot 'FamilyMEP.PdfRenderer\FamilyMEP.PdfRenderer.csproj'
$pluginOutput = Join-Path $PSScriptRoot 'plugin-output'
$rendererOutput = Join-Path $pluginOutput 'Tools\PdfRenderer'

& $dotnet restore $pluginProject --configfile $nugetConfig --ignore-failed-sources --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet restore $rendererProject --configfile $nugetConfig --ignore-failed-sources --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build $pluginProject --configuration Debug --output $pluginOutput --no-restore `
    --verbosity quiet --nologo '-consoleloggerparameters:ErrorsOnly'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build $rendererProject --configuration Release --output $rendererOutput --no-restore `
    --verbosity quiet --nologo '-consoleloggerparameters:ErrorsOnly'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$pdfiumSource = Join-Path $portableRoot 'python312\Lib\site-packages\pypdfium2_raw\pdfium.dll'
if (-not (Test-Path -LiteralPath $pdfiumSource)) {
    Write-Error "PDFium native library is missing: $pdfiumSource"
    exit 1
}
Copy-Item -LiteralPath $pdfiumSource -Destination (Join-Path $rendererOutput 'pdfium.dll') -Force
exit 0
