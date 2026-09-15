[CmdletBinding()]
param(
    [string]$Package = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\LanView-0.1.0-win-x64.zip'),
    [Parameter(Mandatory)][string]$MoonlightArchive,
    [string]$MoonlightSourceArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-That {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-EntryHash {
    param([IO.Compression.ZipArchiveEntry]$Entry)
    $stream = $Entry.Open()
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
}

function Read-Entry {
    param([IO.Compression.ZipArchiveEntry]$Entry)
    $reader = [IO.StreamReader]::new($Entry.Open())
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

Assert-That ((Get-FileHash -LiteralPath $MoonlightArchive -Algorithm SHA256).Hash -eq '95F4D0853A31C7FCED4B6D233DDF55EE41720963F2E2620A9CB49A21D112AED1') 'The comparison Moonlight archive is not the pinned official release.'
$packageArchive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package).Path)
$upstreamArchive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $MoonlightArchive).Path)
try {
    $entries = @{}
    foreach ($entry in $packageArchive.Entries) {
        if (-not $entry.Name) { continue }
        Assert-That (-not $entries.ContainsKey($entry.FullName)) 'Duplicate package entry.'
        Assert-That ($entry.FullName -notmatch '(^|[\/])\.\.([\/]|$)|^[\/]') 'Unsafe package path.'
        Assert-That ($entry.FullName -notmatch '(^|/)(bin|obj|build|\.build|__pycache__|\.git|node_modules)/') 'A generated build directory entered the package.'
        Assert-That ($entry.Name -notmatch '^(profile|credentials?|sunshine_state|identity)\.(json|xml|ini)$|\.(pem|key|pfx|p12|log|dmp)$') 'Local state or credential file entered the package.'
        $entries[$entry.FullName] = $entry
    }
    foreach ($required in @('LanView.exe', 'tools/Moonlight/Moonlight.exe', 'tools/Moonlight/portable.dat', 'host/lanview-host', 'host/README.md', 'README.md', 'SOURCE.md', 'THIRD_PARTY.md', 'bundle.json', 'checksums.sha256', 'licenses/moonlight/LICENSE', 'licenses/microsoft.netcore.app.runtime.win-x64/LICENSE.TXT', 'licenses/microsoft.netcore.app.runtime.win-x64/THIRD-PARTY-NOTICES.TXT', 'licenses/microsoft.windowsdesktop.app.runtime.win-x64/LICENSE', 'source/LanView/src/LanView.Windows/LanView.Windows.csproj')) {
        Assert-That ($entries.ContainsKey($required)) "Required package entry missing: $required"
    }
    $manifest = Read-Entry $entries['bundle.json'] | ConvertFrom-Json
    Assert-That ($manifest.selfContained -and -not $manifest.trimmed -and $manifest.dotnetRuntime -eq '10.0.11') 'The runtime manifest does not describe the requested untrimmed self-contained build.'
    $moonlightFiles = 0
    foreach ($entry in $upstreamArchive.Entries) {
        if (-not $entry.Name) { continue }
        $target = 'tools/Moonlight/' + $entry.FullName
        Assert-That ($entries.ContainsKey($target)) "Official Moonlight file missing: $($entry.FullName)"
        Assert-That ((Get-EntryHash $entry) -eq (Get-EntryHash $entries[$target])) "Official Moonlight file changed: $($entry.FullName)"
        $moonlightFiles++
    }
    $packagedMoonlight = @($entries.Keys | Where-Object { $_.StartsWith('tools/Moonlight/') })
    Assert-That ($packagedMoonlight.Count -eq $moonlightFiles) 'Extra Moonlight files, settings or state entered the package.'

    $recorded = @{}
    foreach ($line in (Read-Entry $entries['checksums.sha256']) -split '\r?\n') {
        if (-not $line) { continue }
        Assert-That ($line -match '^([0-9A-Fa-f]{64})  (.+)$') 'Invalid checksum record.'
        $hash = $Matches[1]
        $name = $Matches[2]
        Assert-That ($entries.ContainsKey($name) -and -not $recorded.ContainsKey($name)) 'Checksum references a missing or duplicate file.'
        Assert-That ((Get-EntryHash $entries[$name]) -eq $hash) "Package checksum mismatch: $name"
        $recorded[$name] = $true
    }
    Assert-That ($recorded.Count -eq $entries.Count - 1) 'Package checksum inventory is incomplete.'
    $protocolDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'host\clipboard\protocols'
    if (Test-Path -LiteralPath $protocolDirectory -PathType Container) {
        foreach ($protocol in Get-ChildItem -LiteralPath $protocolDirectory -Filter '*.xml' -File) {
            Assert-That ($entries.ContainsKey('host/clipboard/protocols/' + $protocol.Name)) "Clipboard protocol missing: $($protocol.Name)"
        }
    }
    if (-not $MoonlightSourceArchive) { $MoonlightSourceArchive = Join-Path (Split-Path -Parent $Package) $manifest.moonlightSource.archive }
    Assert-That ((Get-FileHash -LiteralPath $MoonlightSourceArchive -Algorithm SHA256).Hash -eq $manifest.moonlightSource.sha256) 'Companion source archive is missing or changed.'
    [pscustomobject]@{ Result = 'PASS'; PackageFiles = $entries.Count; UnmodifiedMoonlightFiles = $moonlightFiles; ChecksumsVerified = $recorded.Count; ProtocolSourcesIncluded = $true; NetworkOrUiUsed = $false } | Format-List
} finally {
    $packageArchive.Dispose()
    $upstreamArchive.Dispose()
}
