param([switch]$SkipSdk)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsRoot = Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null

if (-not $SkipSdk -and -not (Test-Path (Join-Path $toolsRoot 'dotnet/dotnet.exe'))) {
    Write-Output 'Downloading the official .NET 10 SDK into this project...'
    $metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $sdkVersion = $metadata.'latest-sdk'
    $release = $metadata.releases | Where-Object { $_.sdk.version -eq $sdkVersion } | Select-Object -First 1
    $sdkFile = $release.sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
    if (-not $sdkFile) { throw 'Could not resolve the official SDK download.' }
    $archivePath = Join-Path $toolsRoot 'dotnet-sdk.zip'
    Invoke-WebRequest $sdkFile.url -OutFile $archivePath
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash
    if ($actualHash -ne $sdkFile.hash) { throw 'SDK checksum verification failed.' }
    Expand-Archive -LiteralPath $archivePath -DestinationPath (Join-Path $toolsRoot 'dotnet') -Force
    Remove-Item -LiteralPath $archivePath
    Write-Output "Installed .NET SDK $sdkVersion locally."
}

if (-not (Test-Path (Join-Path $toolsRoot 'ffmpeg/ffmpeg.exe'))) {
    Write-Output 'Downloading the FFmpeg essentials build...'
    $archivePath = Join-Path $toolsRoot 'ffmpeg.zip'
    $downloadUrl = 'https://github.com/GyanD/codexffmpeg/releases/download/9.1/ffmpeg-9.1-essentials_build.zip'
    $expectedHash = 'b9bbbb3acece000cbeec994f856649354e52c773807841e78908374b24d658fd'
    Invoke-WebRequest $downloadUrl -OutFile $archivePath
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'FFmpeg checksum verification failed.'
    }
    $extractRoot = Join-Path $toolsRoot 'ffmpeg-extract'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force
    $binaryRoot = (Get-ChildItem -LiteralPath $extractRoot -Filter ffmpeg.exe -Recurse | Select-Object -First 1).Directory.FullName
    $destination = Join-Path $toolsRoot 'ffmpeg'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -LiteralPath (Join-Path $binaryRoot 'ffmpeg.exe') -Destination $destination
    Copy-Item -LiteralPath (Join-Path $binaryRoot 'ffprobe.exe') -Destination $destination
    $distributionRoot = Split-Path $binaryRoot -Parent
    Get-ChildItem -LiteralPath $distributionRoot -File | Copy-Item -Destination $destination
    $resolvedExtract = [IO.Path]::GetFullPath($extractRoot)
    if (-not $resolvedExtract.StartsWith($toolsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Extraction directory escaped this project.'
    }
    Remove-Item -LiteralPath $resolvedExtract -Recurse -Force
    Remove-Item -LiteralPath $archivePath
    Write-Output 'Installed and verified FFmpeg.'
}
Write-Output 'Ready. Run scripts/build.ps1 to build Rewind.'
