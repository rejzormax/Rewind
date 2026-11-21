$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$configuration = Get-Content -LiteralPath (Join-Path $projectRoot 'rewind-branding.json') -Raw | ConvertFrom-Json
$applicationId = ([string]$configuration.discordApplicationId).Trim()
$numericId = [uint64]0
if ($applicationId -notmatch '^[0-9]{17,20}$' -or -not [uint64]::TryParse($applicationId, [ref]$numericId) -or $numericId -eq 0 -or
    $applicationId -eq '123456789012345678' -or $applicationId -match '^([0-9])\1+$') {
    throw 'A real Rewind Discord application ID must be committed once in rewind-branding.json before publishing a public release. End users do not configure Discord RPC.'
}
Write-Output 'Shared Discord identity is configured for all release users.'
