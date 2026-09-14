$portableRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
. (Join-Path $portableRoot 'activate.ps1')

$dotnet = Join-Path $portableRoot 'dotnet8\dotnet.exe'
$nugetConfig = Join-Path $portableRoot 'NuGet.Config'
$loaderProject = Join-Path $PSScriptRoot 'RevitHotLoader2025\RevitHotLoader2025.csproj'
$pluginProject = Join-Path $PSScriptRoot 'FamilyMEP.Plugin\FamilyMEP.Plugin.csproj'
$deploy = Join-Path $PSScriptRoot 'deploy'
$pluginOutput = Join-Path $PSScriptRoot 'plugin-output'

& $dotnet restore $loaderProject --configfile $nugetConfig --ignore-failed-sources --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet restore $pluginProject --configfile $nugetConfig --ignore-failed-sources --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build $loaderProject --configuration Release --output $deploy --no-restore `
    --verbosity quiet --nologo '-consoleloggerparameters:ErrorsOnly'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build $pluginProject --configuration Debug --output $pluginOutput --no-restore `
    --verbosity quiet --nologo '-consoleloggerparameters:ErrorsOnly'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'hotloader.json') `
    -Destination (Join-Path $deploy 'hotloader.json') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RevitHotLoader2025.addin.template') `
    -Destination (Join-Path $deploy 'FamilyMEP2025.addin') -Force

Write-Host "Loader: $deploy"
Write-Host "Plugin: $pluginOutput"
