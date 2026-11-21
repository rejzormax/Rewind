param([string]$DistributionDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distribution = if ($DistributionDirectory) { [IO.Path]::GetFullPath($DistributionDirectory) } else { Join-Path $projectRoot 'dist/Rewind' }
if (-not (Test-Path (Join-Path $distribution 'Rewind.exe'))) { throw 'Run scripts/build.ps1 first.' }
if (-not (Test-Path (Join-Path $distribution 'auto-updater.exe'))) { throw 'The release package is missing auto-updater.exe.' }
$archivePath = Join-Path $projectRoot 'dist/Rewind-Windows-x64.zip'
Compress-Archive -LiteralPath $distribution -DestinationPath $archivePath -CompressionLevel Optimal -Force
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256 | Format-List
(Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() + '  Rewind-Windows-x64.zip' | Set-Content -LiteralPath ($archivePath + '.sha256') -Encoding ascii
Write-Output "Packaged: $archivePath"
