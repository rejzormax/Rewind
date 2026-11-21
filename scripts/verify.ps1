param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetCommand = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnetCommand)) { $dotnetCommand = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget'
$project = Join-Path $projectRoot 'tests/Rewind.Integration/Rewind.Integration.csproj'
if (-not $NoRestore) {
    & $dotnetCommand restore $project --configfile (Join-Path $projectRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Integration restore failed.' }
}
& $dotnetCommand run --project $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed.' }
$fixtureProject = Join-Path $projectRoot 'tests/Rewind.UpdateFixture/Rewind.UpdateFixture.csproj'
if (-not $NoRestore) {
    & $dotnetCommand restore $fixtureProject --configfile (Join-Path $projectRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Updater fixture restore failed.' }
}
& $dotnetCommand publish $fixtureProject -c Release --no-restore -o (Join-Path $projectRoot 'artifacts/update-fixture') -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Updater fixture build failed.' }
& $dotnetCommand run --project $project -c Release --no-build --no-restore -- --check-updater
if ($LASTEXITCODE -ne 0) { throw 'Updater installation checks failed.' }
