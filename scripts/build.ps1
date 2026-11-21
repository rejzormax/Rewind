param([switch]$SelfContained, [switch]$SkipChecks, [switch]$OfficialRelease, [switch]$NoRestore, [switch]$IncludeCaptureEngine, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($OfficialRelease) { $SelfContained = $true }
& (Join-Path $PSScriptRoot 'render-brand.ps1')
$localDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetCommand = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget'
$configuration = Join-Path $PSScriptRoot 'nuget.config'
$target = if ($OutputDirectory) { [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory)) } else { Join-Path $projectRoot 'dist/Rewind' }
$allowedRoots = @((Join-Path $projectRoot 'dist'), (Join-Path $projectRoot 'artifacts'))
if (-not ($allowedRoots | Where-Object { $target.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })) { throw 'Choose an output directory inside dist or artifacts.' }
$output = Join-Path $projectRoot ('artifacts/build-' + [Guid]::NewGuid().ToString('N'))
if (-not $SkipChecks) {
    $checks = Join-Path $projectRoot 'tests/Rewind.Checks/Rewind.Checks.csproj'
    if (-not $NoRestore) {
        & $dotnetCommand restore $checks --configfile $configuration
        if ($LASTEXITCODE -ne 0) { throw 'Check restore failed.' }
    }
    & $dotnetCommand run --project $checks -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
}
foreach ($projectName in @('Rewind','Rewind.Updater')) {
    $project = Join-Path $projectRoot "src/$projectName/$projectName.csproj"
    if (-not $NoRestore) {
        $restore = @('restore', $project, '--configfile', $configuration)
        if ($SelfContained) { $restore += @('-r','win-x64','-p:SelfContained=true','--source','https://api.nuget.org/v3/index.json') }
        & $dotnetCommand @restore
        if ($LASTEXITCODE -ne 0) { throw "$projectName restore failed." }
    }
    $publish = @('publish',$project,'-c','Release','-o',$output,'--no-restore','-p:DebugType=None','-p:DebugSymbols=false')
    if ($SelfContained) {
        $publish += @('-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:EnableCompressionInSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true')
    } else { $publish += @('--self-contained','false') }
    & $dotnetCommand @publish
    if ($LASTEXITCODE -ne 0) { throw "$projectName build failed." }
}
if ($IncludeCaptureEngine) {
    $engine = Join-Path $projectRoot '.tools/ffmpeg'
    if (-not (Test-Path -LiteralPath (Join-Path $engine 'ffmpeg.exe'))) { throw 'Run setup.ps1 before bundling the capture engine.' }
    $engineOutput = Join-Path $output 'tools'
    New-Item -ItemType Directory -Force -Path $engineOutput | Out-Null
    Get-ChildItem -LiteralPath $engine -File | Where-Object { $_.Name -ne 'ffprobe.exe' } | Copy-Item -Destination $engineOutput
}
foreach ($document in @('README.md','LICENSE')) { Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $output }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/THIRD-PARTY-NOTICES.md') -Destination $output
$assetOutput = Join-Path $output 'assets'
New-Item -ItemType Directory -Force -Path $assetOutput | Out-Null
foreach ($asset in @('rewind.ico','LICENSE.txt')) { Copy-Item -LiteralPath (Join-Path $projectRoot "assets/$asset") -Destination $assetOutput }
if ($SelfContained) {
    foreach ($package in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
        $packageDirectory = Join-Path $env:NUGET_PACKAGES $package
        $packageVersion = Get-ChildItem -LiteralPath $packageDirectory -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if ($packageVersion) {
            $licenseOutput = Join-Path $output "licenses/$package"
            New-Item -ItemType Directory -Force -Path $licenseOutput | Out-Null
            Get-ChildItem -LiteralPath $packageVersion.FullName -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' } | Copy-Item -Destination $licenseOutput
        }
    }
}
$backup = $target + '-previous-' + [Guid]::NewGuid().ToString('N')
foreach ($movePath in @($output,$target,$backup)) {
    if (-not [IO.Path]::GetFullPath($movePath).StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'A build path escaped the project.' }
}
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
if (Test-Path -LiteralPath $target) {
    $running = Get-Process -Name Rewind,auto-updater -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($target + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw "Quit the app running from $target before replacing that build." }
    Move-Item -LiteralPath $target -Destination $backup
}
try { Move-Item -LiteralPath $output -Destination $target }
catch {
    if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $backup -Destination $target }
    throw
}
if (Test-Path -LiteralPath $backup) { Write-Output "Previous build: $backup" }
Write-Output "Built: $target/Rewind.exe"
