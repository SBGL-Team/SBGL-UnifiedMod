<#
.SYNOPSIS
    Replaces Approved_Mods.json in the approved-mods gist with the file the build generated.

.DESCRIPTION
    Run after uploading the release zip to Thunderstore. Before writing anything it checks that:
      - the file's SBGL Unified Mod hash is the DLL inside the release zip
      - that version is live on Thunderstore (warns and asks if not)
      - no other mod's entry changed in the gist since the file was generated, so a hand edit
        made in the meantime is not overwritten
    Then shows what changes, asks for confirmation (skip with -Yes), publishes with the GitHub
    CLI, and reads the gist back to confirm.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Publish-ApprovedMods.ps1
    Publishes the file for the version in manifest.json.
#>
param(
    [string] $Version,
    [string] $File,
    [switch] $Yes,
    [double] $GraceHours = 1,
    [string] $ReleaseRoot = 'C:\SBGL-Team',
    [string] $GistId = '59765f02af8dd87179ca920409ff3b27',
    [string] $Guid = 'com.sbgl.unified'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $Version = (Get-Content -Raw (Join-Path $repoRoot 'manifest.json') | ConvertFrom-Json).version_number
}
if (-not $File) { $File = Join-Path $ReleaseRoot "Approved_Mods_$Version.json" }
if (-not (Test-Path $File)) { throw "No generated gist file at $File - build the release first." }

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = 'C:\Program Files\GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) { throw 'GitHub CLI (gh) is not installed.' }

function Get-UnifiedEntry($doc) { @($doc.mods | Where-Object { $_.guid -eq $Guid }) | Select-Object -First 1 }
function Get-OtherMods($doc) { @($doc.mods | Where-Object { $_.guid -ne $Guid }) | ConvertTo-Json -Depth 10 -Compress }

$newText = [IO.File]::ReadAllText($File)
$new = $newText | ConvertFrom-Json
$newEntry = Get-UnifiedEntry $new
if (-not $newEntry) { throw "$File has no '$Guid' entry." }
if ($newEntry.version -ne $Version) { throw "$File is for version $($newEntry.version), not $Version." }
$newHash = @($newEntry.assemblies)[0].sha256

# Never publish unanswered: without a person at the prompt, -Yes is required.
if (-not $Yes -and ([Console]::IsInputRedirected -or -not [Environment]::UserInteractive)) {
    throw 'No one can answer the confirmation prompt here. Run this in a terminal, or pass -Yes.'
}

# 1. The hash being approved must be the DLL players download.
$zip = Join-Path $ReleaseRoot "KingCox22-SBGL_UnifiedMod_$Version.zip"
if (-not (Test-Path $zip)) { throw "Release zip not found: $zip" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $dllEntry = $archive.Entries | Where-Object { $_.Name -eq 'SBGL.UnifiedMod.dll' } | Select-Object -First 1
    $stream = $dllEntry.Open()
    try { $zipHash = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose() }
} finally { $archive.Dispose() }
if ($zipHash -ne $newHash) { throw "Hash mismatch: the gist file approves $newHash but the zip's DLL is $zipHash. Rebuild, upload that zip, then publish." }

# 2. Is this version live on Thunderstore yet?
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$live = $null
try { $live = (Invoke-RestMethod 'https://thunderstore.io/api/experimental/package/KingCox22/SBGL_UnifiedMod/').latest.version_number } catch { }

# 3. Current gist, and anything else that changed in it since the file was generated.
$currentText = (& $gh gist view $GistId --filename Approved_Mods.json --raw) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Could not read the gist with gh. Is gh logged in (gh auth status)?' }
$current = $currentText | ConvertFrom-Json
$currentEntry = Get-UnifiedEntry $current
$othersChanged = (Get-OtherMods $current) -ne (Get-OtherMods $new)

# The grace period starts when the new version is enabled - now - not when it was built.
if ($currentEntry.version -ne $Version) {
    $graceUntil = [DateTime]::UtcNow.AddHours($GraceHours).ToString('yyyy-MM-ddTHH:mm:ssZ')
    foreach ($a in @($newEntry.assemblies)) { if ($a.validUntil) { $a.validUntil = $graceUntil } }
    $newText = (Get-Content -Raw $File) -replace '"validUntil": "[^"]*"', ('"validUntil": "' + $graceUntil + '"')
    [IO.File]::WriteAllText($File, $newText, (New-Object Text.UTF8Encoding $false))
}

Write-Host ''
Write-Host "Approved_Mods.json in gist $GistId"
Write-Host "  SBGL Unified Mod: $($currentEntry.version) -> $($newEntry.version)"
foreach ($a in @($newEntry.assemblies)) {
    $note = if ($a.validUntil) { "valid until $($a.validUntil) ($(([DateTime]::Parse($a.validUntil).ToLocalTime()).ToString('MMM d, h:mm tt')) local)" } else { 'current' }
    Write-Host "    $($a.sha256)  $note"
}
Write-Host "  Release zip DLL matches: yes"
if ($live -eq $Version) { Write-Host "  Thunderstore latest: $live (live)" }
elseif ($live) { Write-Host "  WARNING: Thunderstore latest is $live, not $Version. Upload the zip first, or players can't get it yet." -ForegroundColor Yellow }
else { Write-Host '  WARNING: could not check Thunderstore.' -ForegroundColor Yellow }
if ($othersChanged) {
    Write-Host '  WARNING: other mods in the gist differ from the generated file (edited since the build?).' -ForegroundColor Yellow
    Write-Host '           Publishing would replace them with the build-time copy. Rebuild to pick up the edit.' -ForegroundColor Yellow
}
Write-Host ''

if (-not $Yes) {
    $answer = Read-Host 'Publish this to the gist? (y/N)'
    if (-not ($answer -is [string]) -or $answer.Trim().ToLowerInvariant() -notin @('y', 'yes')) { Write-Host 'Not published.'; exit 1 }
} elseif ($othersChanged -or $live -ne $Version) {
    throw 'Refusing to publish with -Yes while there are warnings. Run without -Yes to review them.'
}

& $gh gist edit $GistId --filename Approved_Mods.json $File
if ($LASTEXITCODE -ne 0) { throw 'gh gist edit failed.' }

$after = (& $gh gist view $GistId --filename Approved_Mods.json --raw) -join "`n" | ConvertFrom-Json
if (@((Get-UnifiedEntry $after).assemblies)[0].sha256 -ne $newHash) { throw 'Published, but reading the gist back did not show the new hash.' }
Write-Host "Published. The gist now approves SBGL Unified Mod $Version." -ForegroundColor Green
Write-Host 'Players pick it up on their next sync (a few minutes) or after restarting the game.'
