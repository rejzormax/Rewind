$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = Join-Path $projectRoot ('artifacts/public-source-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($name in @('.gitignore','LICENSE','README.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $destination }
foreach ($name in @('rewind.svg','rewind.ico','rewind.png','rewind-icon.png','rewind-transparent.png','rewind-preview.png','LICENSE.txt')) {
    $source = Join-Path $projectRoot "assets/$name"
    if (Test-Path -LiteralPath $source) {
        $assetTarget = Join-Path $destination 'assets'
        New-Item -ItemType Directory -Force -Path $assetTarget | Out-Null
        Copy-Item -LiteralPath $source -Destination $assetTarget
    }
}
foreach ($folder in @('src','tests','scripts','.github','docs','installer')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName)
        if ($relative -match '(^|[\\/])(bin|obj|\.git|\.vs)([\\/]|$)' -or
            $relative -eq 'src\Rewind\Resources\Logo.xaml' -or $relative -eq 'src/Rewind/Resources/Logo.xaml' -or
            $file.Name -like '.env*' -or $file.Extension -in @('.key','.pfx','.p12','.snk','.log','.user')) { continue }
        if ($file.Extension -notin @('.cs','.csproj','.xaml','.manifest','.ps1','.md','.yml','.yaml','.xml','.json','.props','.config','.iss','.txt','.cmd')) { continue }
        $target = Join-Path $destination $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'dist') | Out-Null
$archivePath = Join-Path $projectRoot 'dist/Rewind-Public-Source.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath }
[IO.Compression.ZipFile]::CreateFromDirectory($destination,$archivePath,[IO.Compression.CompressionLevel]::Optimal,$false)
[IO.File]::WriteAllText((Join-Path $projectRoot 'artifacts/public-source-path.txt'),$destination)
Write-Output "Public source checkout: $destination"
Write-Output "Public source archive: $archivePath"
