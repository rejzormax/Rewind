$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = Join-Path $projectRoot ('artifacts/public-source-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$rootFiles = @('.gitignore','Directory.Build.props','Launch Rewind.cmd','LICENSE','NuGet.Config','README.md','rewind-branding.json','rewind-updates.json','THIRD-PARTY-NOTICES.md','PUBLISHING.md','RELEASE.md','BRANDING.md')
foreach ($name in $rootFiles) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $destination }
foreach ($folder in @('src','tests','scripts','.github')) {
    $sourceRoot = Join-Path $projectRoot $folder
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName)
        if ($relative -match '(^|[\\/])(bin|obj|\.git|\.vs)([\\/]|$)' -or
            $relative -eq 'src\Rewind\Resources\Logo.xaml' -or $relative -eq 'src/Rewind/Resources/Logo.xaml' -or
            $file.Name -like '.env*' -or $file.Extension -in @('.key','.pfx','.p12','.snk','.log','.user')) { continue }
        if ($file.Extension -notin @('.cs','.csproj','.xaml','.manifest','.ps1','.md','.yml','.yaml','.xml','.json')) { continue }
        $target = Join-Path $destination $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
$archivePath = Join-Path $projectRoot 'dist/Rewind-Public-Source.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath }
[IO.Compression.ZipFile]::CreateFromDirectory($destination,$archivePath,[IO.Compression.CompressionLevel]::Optimal,$false)
[IO.File]::WriteAllText((Join-Path $projectRoot 'artifacts/public-source-path.txt'),$destination)
Write-Output "Public source checkout: $destination"
Write-Output "Public source archive: $archivePath"
