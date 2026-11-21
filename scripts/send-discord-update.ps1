param([string]$ReleaseTag = '')
$ErrorActionPreference = 'Stop'
$webhook = $env:DISCORD_GITHUB_WEBHOOK
if (-not $webhook) { Write-Output 'Discord channel webhook is not configured.'; exit 0 }
$uri = [Uri]$webhook
if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('discord.com','discordapp.com') -or $uri.AbsolutePath -notmatch '^/api(/v[0-9]+)?/webhooks/[0-9]+/[^/]+/?$') {
    throw 'The configured Discord webhook is invalid.'
}
if ($ReleaseTag) {
    $title = "Rewind $ReleaseTag released"
    $description = 'The Windows download is ready. Open the release to see the changes and download Rewind.'
    $link = "https://github.com/$env:GITHUB_REPOSITORY/releases/tag/$([Uri]::EscapeDataString($ReleaseTag))"
} elseif ($env:GITHUB_EVENT_NAME -eq 'workflow_dispatch') {
    $title = 'Rewind GitHub updates connected'
    $description = 'Source updates and new releases will appear in this channel.'
    $link = "https://github.com/$env:GITHUB_REPOSITORY"
} else {
    $event = Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
}
if (-not $ReleaseTag -and $env:GITHUB_EVENT_NAME -eq 'release') {
    $title = "Rewind $($event.release.tag_name) released"
    $description = [string]$event.release.body
    $link = [string]$event.release.html_url
} elseif (-not $ReleaseTag -and $env:GITHUB_EVENT_NAME -eq 'push') {
    $branch = ([string]$event.ref).Replace('refs/heads/','')
    $title = "Rewind updated: $branch"
    $description = [string]$event.head_commit.message
    $link = [string]$event.compare
}
if ($title.Length -gt 240) { $title = $title.Substring(0,240) }
if ($description.Length -gt 1800) { $description = $description.Substring(0,1800) }
$payload = @{ username = 'Rewind updates'; allowed_mentions = @{ parse = @() }; embeds = @(@{ title = $title; description = $description; url = $link; color = 16777215 }) } | ConvertTo-Json -Depth 8
try { Invoke-RestMethod -Uri $uri -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($payload)) | Out-Null }
catch { throw 'The Discord update notification could not be delivered.' }
Write-Output 'Delivered the repository update to the configured Discord channel.'
