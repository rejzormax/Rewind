param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build-installer.ps1') -TestMode }
$setupPath = Join-Path $projectRoot 'dist/Rewind-Setup-Test.exe'
if (-not (Test-Path -LiteralPath $setupPath)) { throw 'Build the test installer first.' }
$testRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ('artifacts/setup-smoke-' + [Guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith((Join-Path $projectRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Installer test path escaped the workspace.' }
[IO.File]::WriteAllText((Join-Path $projectRoot 'artifacts/setup-smoke-path.txt'), $testRoot)
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',('/DIR="' + $testRoot + '"'),('/LOG="' + (Join-Path $projectRoot 'artifacts/setup-smoke.log') + '"'))
$setup = Start-Process -FilePath $setupPath -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($setup.ExitCode -ne 0) { throw "Setup failed with exit code $($setup.ExitCode). See artifacts/setup-smoke.log." }
foreach ($name in @('Rewind.exe','auto-updater.exe','unins000.exe','.test-data/engine/ffmpeg.exe','.test-data/engine/LICENSE','.test-data/engine/README.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $testRoot $name))) { throw "Setup did not prepare $name." }
}
Write-Output 'PASS Setup installs the standalone app, updater and uninstaller, and downloads verified recording files.'
$dataRoot = Join-Path $testRoot '.test-data'
[IO.File]::WriteAllText((Join-Path $dataRoot 'settings.json'), '{"MinimizeToTray":false,"DiscordEnabled":false,"CheckForUpdates":false}')
New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot 'clips') | Out-Null
[IO.File]::WriteAllText((Join-Path $dataRoot 'clips/keep.mp4'), 'keep recorded clip fixture')
$launchId = [Guid]::NewGuid().ToString('N')
$launchArguments = @('--data-dir', ('"' + $dataRoot + '"'), '--test-source', '--skip-update', '--update-launch-id', $launchId)
$app = Start-Process -FilePath (Join-Path $testRoot 'Rewind.exe') -ArgumentList $launchArguments -PassThru -WindowStyle Hidden
try {
    $ack = Join-Path $dataRoot ('updates/started-' + $launchId + '.json')
    $timeout = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $ack)) {
        if ($app.HasExited -or $timeout.Elapsed.TotalSeconds -gt 15) { throw 'The installed app did not acknowledge startup.' }
        Start-Sleep -Milliseconds 100
    }
    $started = Get-Content -LiteralPath $ack -Raw | ConvertFrom-Json
    if ($started.ProcessId -ne $app.Id) { throw 'The startup acknowledgement belongs to another process.' }
    Write-Output 'PASS The installed standalone Rewind app opens and confirms its version.'
} finally {
    if (-not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(10000)) { $app.Kill(); $app.WaitForExit() }
    }
}
$uninstallArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $projectRoot 'artifacts/uninstall-smoke.log') + '"'))
$uninstaller = Start-Process -FilePath (Join-Path $testRoot 'unins000.exe') -ArgumentList $uninstallArguments -Wait -PassThru -WindowStyle Hidden
if ($uninstaller.ExitCode -ne 0 -or (Test-Path -LiteralPath (Join-Path $testRoot 'Rewind.exe'))) { throw 'Uninstall did not remove the app.' }
if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'settings.json')) -or [IO.File]::ReadAllText((Join-Path $dataRoot 'clips/keep.mp4')) -ne 'keep recorded clip fixture') { throw 'Uninstall changed settings or recorded clips.' }
Write-Output 'PASS Uninstall removes the app and preserves settings and recorded clips.'
