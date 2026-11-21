param([string]$ReleaseTag = '')
$ErrorActionPreference = 'Stop'
$webhook = [string]$env:DISCORD_GITHUB_WEBHOOK
if ([string]::IsNullOrWhiteSpace($webhook)) {
    throw 'Missing repository Actions secret DISCORD_GITHUB_WEBHOOK. Add the channel webhook under Settings > Secrets and variables > Actions > Repository secrets.'
}
$uri = $null
if (-not [Uri]::TryCreate($webhook.Trim(), [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or
    $uri.Host -notin @('discord.com','discordapp.com') -or -not $uri.IsDefaultPort -or $uri.UserInfo) {
    throw 'DISCORD_GITHUB_WEBHOOK must contain the full HTTPS URL copied from Discord > Channel settings > Integrations > Webhooks.'
}
$builder = [UriBuilder]::new($uri)
$path = $uri.AbsolutePath.TrimEnd('/')
if ($path.EndsWith('/github', [StringComparison]::OrdinalIgnoreCase)) { $path = $path.Substring(0, $path.Length - 7) }
if ($path -notmatch '^/api(/v[0-9]+)?/webhooks/[0-9]+/[^/]+$') { throw 'The stored Discord webhook URL is invalid. Copy its full URL again from the channel integrations.' }
$builder.Path = $path
$query = @($uri.Query.TrimStart('?') -split '&' | Where-Object { $_ -and $_ -notmatch '^wait=' })
$builder.Query = (@($query) + 'wait=true') -join '&'
$builder.Fragment = ''
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
    if ($env:GITHUB_EVENT_NAME -eq 'release') {
        $title = "Rewind $($event.release.tag_name) released"
        $description = [string]$event.release.body
        $link = [string]$event.release.html_url
    } elseif ($env:GITHUB_EVENT_NAME -eq 'push') {
        $branch = ([string]$event.ref).Replace('refs/heads/','')
        $title = "Rewind updated: $branch"
        $description = [string]$event.head_commit.message
        $link = [string]$event.compare
    } else { throw 'This workflow supports pushes, published releases, and manual connection tests.' }
}
if ($title.Length -gt 240) { $title = $title.Substring(0,240) }
if ($description.Length -gt 1800) { $description = $description.Substring(0,1800) }
$payload = @{ username = 'Rewind updates'; allowed_mentions = @{ parse = @() }; embeds = @(@{ title = $title; description = $description; url = $link; color = 16777215 }) } | ConvertTo-Json -Depth 8
try {
    $message = Invoke-RestMethod -Uri $builder.Uri -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 30
} catch {
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    if ($status -eq 404) { throw 'Discord returned HTTP 404: the webhook was deleted or its stored URL is incorrect. Replace DISCORD_GITHUB_WEBHOOK with the channel webhook URL.' }
    if ($status -in @(401,403)) { throw "Discord returned HTTP ${status}: the webhook is not authorized. Copy a new webhook URL from the intended channel." }
    if ($status -eq 429) { throw 'Discord temporarily rate-limited the webhook. Retry this workflow shortly.' }
    if ($status) { throw "Discord returned HTTP $status while posting the update." }
    throw 'Discord could not be reached. Retry the workflow and check that the saved webhook URL is complete.'
}
if (-not $message.id -or -not $message.channel_id) { throw 'Discord did not confirm a saved message. Check the webhook and rerun the manual connection test.' }
Write-Output "Discord confirmed delivery to channel $($message.channel_id)."
if ($env:GITHUB_STEP_SUMMARY) {
    "Discord confirmed message delivery to channel ID $($message.channel_id). Check that this is your github-updates channel." | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
