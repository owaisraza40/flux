param(
  [string]$Owner = 'owaisraza40',
  [string]$Repository = 'flux',
  [string]$Tag = 'v1.0.0',
  [switch]$Validated
)
$ErrorActionPreference = 'Stop'
if (-not $Validated) { throw 'Run the build, engine/bridge tests, and Windows UI/installer checks first, then pass -Validated.' }
if ($Owner -notmatch '^[A-Za-z0-9-]+$' -or $Repository -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository identity.' }
$sfRoot = Split-Path -Parent $PSScriptRoot
$sfReleaseDirectory = Join-Path $sfRoot 'artifacts\release'
$sfExpectedFiles = @('Flux-Setup-1.0.0-win-x64.exe','Flux-1.0.0-win-x64-portable.zip','Flux-Browser-Extension-1.0.0.zip','Flux-Chromium-1.0.0.crx','Flux-Chromium-1.0.0.zip','Flux-Firefox-1.0.0.xpi','SHA256SUMS.txt')
foreach ($sfExpectedFile in $sfExpectedFiles) { if (-not (Test-Path -LiteralPath (Join-Path $sfReleaseDirectory $sfExpectedFile))) { throw "Missing release asset: $sfExpectedFile" } }

# Use the operating system's existing Git helper. Keep credentials in memory;
# never print them, write them to the repository, or put them in a remote URL.
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'Never'
$sfCredentialLines = "protocol=https`nhost=github.com`nusername=$Owner`n`n" | git -c credential.interactive=never credential fill
if ($LASTEXITCODE -ne 0) { throw 'No existing GitHub credential is available to the Git helper.' }
$sfCredential = @{}
foreach ($sfCredentialLine in $sfCredentialLines) { $sfCredentialPair = $sfCredentialLine -split '=',2; if ($sfCredentialPair.Length -eq 2) { $sfCredential[$sfCredentialPair[0]] = $sfCredentialPair[1] } }
if (-not $sfCredential.password) { throw 'Git helper did not return a credential.' }
$sfHeaders = @{ Authorization = "Bearer $($sfCredential.password)"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'Flux-Release' }
$sfProfile = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $sfHeaders -TimeoutSec 30
if ($sfProfile.login -ne $Owner) { throw "Authenticated account does not match requested owner $Owner." }

$sfRepoUrl = "https://api.github.com/repos/$Owner/$Repository"
$sfRepo = $null
try { $sfRepo = Invoke-RestMethod -Uri $sfRepoUrl -Headers $sfHeaders -TimeoutSec 30 }
catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
if (-not $sfRepo) {
  $sfRepoBody = @{ name=$Repository; description='An open-source Windows download manager with segmented transfers, resumable downloads, queues and browser handoff.'; private=$false; auto_init=$false; has_issues=$true; has_wiki=$false } | ConvertTo-Json
  $sfRepo = Invoke-RestMethod -Uri 'https://api.github.com/user/repos' -Method Post -Headers $sfHeaders -ContentType 'application/json' -Body $sfRepoBody -TimeoutSec 60
}
if ($sfRepo.owner.login -ne $Owner) { throw 'Repository owner mismatch.' }
$sfRemoteUrl = "https://github.com/$Owner/$Repository.git"

function Invoke-SfGit { & git -c "safe.directory=$sfRoot" -C $sfRoot @args; if ($LASTEXITCODE -ne 0) { throw "Git operation failed: $args" } }
if (-not (Test-Path -LiteralPath (Join-Path $sfRoot '.git'))) { Invoke-SfGit init -b main }
Invoke-SfGit config user.name 'Flux contributors'
Invoke-SfGit config user.email "$($sfProfile.id)+$Owner@users.noreply.github.com"
$sfExistingOrigin = & git -c "safe.directory=$sfRoot" -C $sfRoot remote get-url origin 2>$null
if ($LASTEXITCODE -eq 0) { if ($sfExistingOrigin -ne $sfRemoteUrl) { throw 'Existing origin points to a different repository.' } }
else { Invoke-SfGit remote add origin $sfRemoteUrl }
Invoke-SfGit add --all
$sfChanges = & git -c "safe.directory=$sfRoot" -C $sfRoot status --porcelain
if ($sfChanges) { Invoke-SfGit commit -m 'feat: release Flux 1.0 Windows download manager' }
Invoke-SfGit push --set-upstream origin main
$sfTagExists = & git -c "safe.directory=$sfRoot" -C $sfRoot tag --list $Tag
if (-not $sfTagExists) { Invoke-SfGit tag -a $Tag -m "Flux $Tag" }
Invoke-SfGit push origin $Tag

$sfRelease = $null
try { $sfRelease = Invoke-RestMethod -Uri "$sfRepoUrl/releases/tags/$Tag" -Headers $sfHeaders -TimeoutSec 30 }
catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
if (-not $sfRelease) {
  $sfNotes = Get-Content -LiteralPath (Join-Path $sfRoot 'docs\RELEASE-NOTES.md') -Raw
  $sfReleaseBody = @{ tag_name=$Tag; target_commitish='main'; name="Flux $Tag — Windows download manager"; body=$sfNotes; draft=$false; prerelease=$false } | ConvertTo-Json -Depth 5
  $sfRelease = Invoke-RestMethod -Uri "$sfRepoUrl/releases" -Method Post -Headers $sfHeaders -ContentType 'application/json' -Body $sfReleaseBody -TimeoutSec 60
}
foreach ($sfAssetName in $sfExpectedFiles) {
  if ($sfRelease.assets | Where-Object { $_.name -eq $sfAssetName }) { Write-Output "Asset already present: $sfAssetName"; continue }
  $sfUploadBase = $sfRelease.upload_url -replace '\{.*$',''
  $sfUploadUrl = $sfUploadBase + '?name=' + [Uri]::EscapeDataString($sfAssetName)
  $sfUploadResult = Invoke-RestMethod -Uri $sfUploadUrl -Method Post -Headers $sfHeaders -ContentType 'application/octet-stream' -InFile (Join-Path $sfReleaseDirectory $sfAssetName) -TimeoutSec 900
  Write-Output "Uploaded: $($sfUploadResult.name) ($($sfUploadResult.size) bytes)"
}
Write-Output "Repository: $($sfRepo.html_url)"
Write-Output "Release: $($sfRelease.html_url)"
$sfCredential.Clear(); $sfHeaders.Clear(); $sfCredentialLines = $null
