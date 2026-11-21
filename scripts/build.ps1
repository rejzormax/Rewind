param([switch]$SelfContained, [switch]$SkipChecks, [switch]$OfficialRelease, [switch]$NoRestore, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($OfficialRelease) { & (Join-Path $PSScriptRoot 'validate-branding.ps1') }
& (Join-Path $PSScriptRoot 'render-brand.ps1')
$localDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetCommand = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget'
$targetOutputRoot = if ($OutputDirectory) { [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory)) } else { Join-Path $projectRoot 'dist/Rewind' }
$allowedOutputRoots = @((Join-Path $projectRoot 'dist'), (Join-Path $projectRoot 'artifacts'))
if (-not ($allowedOutputRoots | Where-Object { $targetOutputRoot.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })) {
    throw 'Choose a build output directory inside this project''s dist or artifacts folder.'
}
$outputRoot = Join-Path $projectRoot ('artifacts/build-' + [Guid]::NewGuid().ToString('N'))
if (-not $SkipChecks) {
    $checksProject = Join-Path $projectRoot 'tests/Rewind.Checks/Rewind.Checks.csproj'
    if (-not $NoRestore) {
        & $dotnetCommand restore $checksProject --configfile (Join-Path $projectRoot 'NuGet.Config')
        if ($LASTEXITCODE -ne 0) { throw 'Check restore failed.' }
    }
    & $dotnetCommand run --project $checksProject -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
}
$appProject = Join-Path $projectRoot 'src/Rewind/Rewind.csproj'
$restoreArgs = @('restore', $appProject, '--configfile', (Join-Path $projectRoot 'NuGet.Config'))
if ($SelfContained) { $restoreArgs += @('-r', 'win-x64', '-p:SelfContained=true', '--source', 'https://api.nuget.org/v3/index.json') }
if (-not $NoRestore) {
    & $dotnetCommand @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'App restore failed.' }
}
$publishArgs = @('publish', $appProject, '-c', 'Release', '-o', $outputRoot, '--no-restore',
    '-p:DebugType=None', '-p:DebugSymbols=false')
if ($SelfContained) {
    $publishArgs += @('-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true')
} else {
    $publishArgs += @('--self-contained', 'false')
}
& $dotnetCommand @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$updaterProject = Join-Path $projectRoot 'src/Rewind.Updater/Rewind.Updater.csproj'
$updaterRestoreArgs = @('restore', $updaterProject, '--configfile', (Join-Path $projectRoot 'NuGet.Config'))
if ($SelfContained) { $updaterRestoreArgs += @('-r','win-x64','-p:SelfContained=true','--source','https://api.nuget.org/v3/index.json') }
if (-not $NoRestore) {
    & $dotnetCommand @updaterRestoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Updater restore failed.' }
}
$updaterPublishArgs = @('publish',$updaterProject,'-c','Release','-o',$outputRoot,'--no-restore','-p:DebugType=None','-p:DebugSymbols=false')
if ($SelfContained) { $updaterPublishArgs += @('-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:EnableCompressionInSingleFile=true') }
else { $updaterPublishArgs += @('--self-contained','false') }
& $dotnetCommand @updaterPublishArgs
if ($LASTEXITCODE -ne 0) { throw 'Updater build failed.' }
$binaryRoot = Join-Path $projectRoot '.tools/ffmpeg'
if (Test-Path (Join-Path $binaryRoot 'ffmpeg.exe')) {
    $destination = Join-Path $outputRoot 'tools'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Get-ChildItem -LiteralPath $binaryRoot -File | Where-Object { $_.Name -ne 'ffprobe.exe' } | Copy-Item -Destination $destination -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'BRANDING.md') -Destination $outputRoot -Force
$assetDestination = Join-Path $outputRoot 'assets'
New-Item -ItemType Directory -Force -Path $assetDestination | Out-Null
foreach ($assetName in @('rewind.svg','rewind.png','rewind-transparent.png','rewind-preview.png','rewind.ico')) {
    $assetPath = Join-Path $projectRoot "assets/$assetName"
    if (Test-Path -LiteralPath $assetPath) { Copy-Item -LiteralPath $assetPath -Destination $assetDestination -Force }
}
$backupRoot = $targetOutputRoot + '-previous-' + [Guid]::NewGuid().ToString('N')
foreach ($movePath in @($outputRoot, $targetOutputRoot, $backupRoot)) {
    if (-not [IO.Path]::GetFullPath($movePath).StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A build path escaped the project.'
    }
}
New-Item -ItemType Directory -Force -Path (Split-Path $targetOutputRoot -Parent) | Out-Null
if (Test-Path -LiteralPath $targetOutputRoot) {
    $runningBuild = Get-Process -Name Rewind,auto-updater -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($targetOutputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    }
    if ($runningBuild) { throw "Quit the app running from $targetOutputRoot before replacing that build." }
    Move-Item -LiteralPath $targetOutputRoot -Destination $backupRoot
}
try { Move-Item -LiteralPath $outputRoot -Destination $targetOutputRoot }
catch {
    if ((Test-Path -LiteralPath $backupRoot) -and -not (Test-Path -LiteralPath $targetOutputRoot)) {
        Move-Item -LiteralPath $backupRoot -Destination $targetOutputRoot
    }
    throw
}
if (Test-Path -LiteralPath $backupRoot) { Write-Output "Previous build: $backupRoot" }
Write-Output "Built: $targetOutputRoot\Rewind.exe"
