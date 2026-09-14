$portableRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
. (Join-Path $portableRoot 'activate.ps1')

$dotnet = Join-Path $portableRoot 'dotnet8\dotnet.exe'
$nugetConfig = Join-Path $portableRoot 'NuGet.Config'
$pluginProject = Join-Path $PSScriptRoot 'tests\GenericPlugin\GenericPlugin.csproj'
$testProject = Join-Path $PSScriptRoot 'tests\UnloadSmokeTest\UnloadSmokeTest.csproj'
$pluginOutput = Join-Path $PSScriptRoot 'tests\output\plugin'
$testOutput = Join-Path $PSScriptRoot 'tests\output\test'
$sourceDll = Join-Path $pluginOutput 'GenericPlugin.dll'
$shadowDll = Join-Path $PSScriptRoot 'tests\output\shadow\GenericPlugin.dll'

& $dotnet restore $pluginProject --configfile $nugetConfig --ignore-failed-sources --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet restore $testProject --configfile $nugetConfig --ignore-failed-sources --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet build $pluginProject --configuration Release --output $pluginOutput --no-restore `
    --verbosity quiet --nologo '-consoleloggerparameters:ErrorsOnly'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet build $testProject --configuration Release --output $testOutput --no-restore `
    --verbosity quiet --nologo '-consoleloggerparameters:ErrorsOnly'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet (Join-Path $testOutput 'UnloadSmokeTest.dll') $sourceDll $shadowDll
exit $LASTEXITCODE

