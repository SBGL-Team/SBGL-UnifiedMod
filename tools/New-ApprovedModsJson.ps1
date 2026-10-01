<#
.SYNOPSIS
    Writes the complete Approved_Mods.json for a new SBGL Unified Mod release, ready to paste
    over the approved-mods gist.

.DESCRIPTION
    Run by the build after the Thunderstore zip is made. Reads the gist as it is now and updates
    only the SBGL Unified Mod entry:
      - the new DLL's hash becomes the current entry
      - the previously released hash is kept with "validUntil", so players on the old version
        stay compliant while the new one becomes downloadable on Thunderstore
      - older grace entries are dropped, and "version" / "generatedAtUtc" are updated
    Rebuilding the same version only swaps the current hash and leaves any grace entry alone.
    Nothing is uploaded; the file is written locally for you to paste into the gist.
#>
param(
    [Parameter(Mandatory = $true)] [string] $DllPath,
    [Parameter(Mandatory = $true)] [string] $Version,
    [Parameter(Mandatory = $true)] [string] $OutFile,
    [double] $GraceHours = 24,
    [string] $Guid = 'com.sbgl.unified',
    [string] $GistUrl = 'https://gist.githubusercontent.com/Kingcox22/59765f02af8dd87179ca920409ff3b27/raw/Approved_Mods.json'
)

$ErrorActionPreference = 'Stop'

# Pretty JSON writer: Windows PowerShell's ConvertTo-Json indents erratically, and this file is
# pasted by hand, so keep it readable.
function ConvertTo-PrettyJson($value, [int] $indent = 0) {
    $pad = '  ' * $indent
    $inner = '  ' * ($indent + 1)
    if ($null -eq $value) { return 'null' }
    if ($value -is [string]) { return '"' + ($value -replace '\\', '\\' -replace '"', '\"') + '"' }
    if ($value -is [bool]) { return $value.ToString().ToLowerInvariant() }
    if ($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal]) {
        return [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $value)
    }
    if ($value -is [System.Collections.IEnumerable]) {
        $items = @($value | ForEach-Object { $inner + (ConvertTo-PrettyJson $_ ($indent + 1)) })
        if ($items.Count -eq 0) { return '[]' }
        return "[`n" + ($items -join ",`n") + "`n$pad]"
    }
    $props = @($value.PSObject.Properties | ForEach-Object { $inner + '"' + $_.Name + '": ' + (ConvertTo-PrettyJson $_.Value ($indent + 1)) })
    return "{`n" + ($props -join ",`n") + "`n$pad}"
}

function Get-UtcTime([string] $text) {
    return [DateTime]::Parse($text, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal)
}

$newHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $DllPath).Hash.ToLowerInvariant()

if ($GistUrl -match '^https?://') {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $raw = (Invoke-WebRequest -UseBasicParsing -Uri ($GistUrl + '?t=' + [DateTime]::UtcNow.Ticks)).Content
} else {
    # A local file, for trying the script out without touching the real gist.
    $raw = [IO.File]::ReadAllText($GistUrl)
}
$doc = $raw | ConvertFrom-Json

$mod = @($doc.mods | Where-Object { $_.guid -eq $Guid }) | Select-Object -First 1
if (-not $mod) { throw "No '$Guid' entry in the approved-mods gist." }

$template = @($mod.assemblies)[0]
$now = [DateTime]::UtcNow

function New-Entry([string] $hash, [string] $validUntil) {
    $entry = [ordered]@{ file = $template.file; relativePath = $template.relativePath; sha256 = $hash }
    if ($validUntil) { $entry.validUntil = $validUntil }
    return [pscustomobject]$entry
}

$current = @($mod.assemblies | Where-Object { -not $_.validUntil -and $_.sha256 -ne $newHash })
$liveGrace = @($mod.assemblies | Where-Object { $_.validUntil -and (Get-UtcTime $_.validUntil) -gt $now -and $_.sha256 -ne $newHash })

if ($mod.version -eq $Version) {
    # Same version rebuilt: replace its hash, keep whatever grace entry is already running.
    $assemblies = @(New-Entry $newHash $null) + @($liveGrace | ForEach-Object { New-Entry $_.sha256 $_.validUntil })
    $graceUntil = $null
    $previousHashes = @()
} else {
    $graceUntil = $now.AddHours($GraceHours).ToString("yyyy-MM-ddTHH:mm:ssZ")
    $previousHashes = @($current | ForEach-Object { $_.sha256 })
    $assemblies = @(New-Entry $newHash $null) + @($previousHashes | ForEach-Object { New-Entry $_ $graceUntil })
}

$previousVersion = $mod.version
$mod.version = $Version
$mod.assemblies = $assemblies
$doc.generatedAtUtc = $now.ToString('o')

$json = ConvertTo-PrettyJson $doc
[IO.File]::WriteAllText($OutFile, $json + "`n", (New-Object Text.UTF8Encoding $false))

Write-Host "Approved-mods gist file: $OutFile"
Write-Host "  SBGL Unified Mod $previousVersion -> $Version, new hash $newHash"
if ($graceUntil) {
    $localUntil = (Get-UtcTime $graceUntil).ToLocalTime().ToString('MMM d, h:mm tt')
    Write-Host "  previous release stays valid until $graceUntil ($localUntil local)"
}
Write-Host "  Paste the whole file over Approved_Mods.json in the gist after uploading the zip."
