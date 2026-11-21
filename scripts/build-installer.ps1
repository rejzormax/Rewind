param([string]$DistributionDirectory = '', [switch]$TestMode)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distribution = if ($DistributionDirectory) { [IO.Path]::GetFullPath($DistributionDirectory) } else { Join-Path $projectRoot 'dist/Rewind' }
foreach ($file in @('Rewind.exe','auto-updater.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $distribution $file))) { throw 'Run build.ps1 -OfficialRelease before creating Setup.' }
}
if (-not $TestMode -and (Test-Path -LiteralPath (Join-Path $distribution 'tools/ffmpeg.exe'))) { throw 'Public Setup downloads its recording engine directly. Rebuild without -IncludeCaptureEngine.' }
$compiler = Join-Path $projectRoot '.tools/innosetup/ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $installed = @((Join-Path ${env:ProgramFiles} 'Inno Setup 7/ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($installed) { $compiler = $installed } else { & (Join-Path $PSScriptRoot 'setup-installer-tools.ps1'); if (-not (Test-Path -LiteralPath $compiler)) { throw 'Installer compiler is missing.' } }
}
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/Rewind.props') -Raw)).Project.PropertyGroup.Version
$output = Join-Path $projectRoot 'dist'
$compilerArguments = @('/Qp',('/DAppVersion=' + $version),('/DSourceDir=' + $distribution),('/DOutputDir=' + $output))
if ($TestMode) { $compilerArguments += '/DInstallerTestMode' }
$compilerArguments += (Join-Path $projectRoot 'installer/rewind.iss')
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
$filename = if ($TestMode) { 'Rewind-Setup-Test.exe' } else { 'Rewind-Setup.exe' }
$setup = Join-Path $output $filename
if (-not (Test-Path -LiteralPath $setup)) { throw 'Setup was not created.' }
((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $filename) | Set-Content -LiteralPath ($setup + '.sha256') -Encoding ascii
Write-Output "Setup: $setup"
