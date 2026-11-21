$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compilerRoot = Join-Path $projectRoot '.tools/innosetup'
$compiler = Join-Path $compilerRoot 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { Write-Output $compiler; return }
$download = Join-Path $projectRoot '.tools/innosetup-7.1.1-x64.exe'
$expectedHash = '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0'
New-Item -ItemType Directory -Force -Path (Split-Path $download -Parent) | Out-Null
Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) { throw 'The installer compiler checksum is invalid.' }
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid') { throw 'The installer compiler signature could not be verified.' }
if (-not [IO.Path]::GetFullPath($compilerRoot).StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Compiler installation escaped the workspace.' }
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/CURRENTUSER',('/DIR="' + $compilerRoot + '"'))
$process = Start-Process -FilePath $download -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) { throw 'Installer compiler setup failed.' }
Remove-Item -LiteralPath $download
Write-Output $compiler
