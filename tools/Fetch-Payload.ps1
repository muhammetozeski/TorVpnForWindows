#Requires -Version 7.0
<#
.SYNOPSIS
    Downloads and verifies the third-party binaries that TorVpnForWindows embeds.

.DESCRIPTION
    The build embeds Tor, sing-box and Wintun into the application executable. Those
    binaries are not committed to the repository; this script fetches them from their
    official distribution points, verifies each archive against a pinned SHA-256 digest
    and stages the required files under <repo>\payload.

    Run this once before building. Re-running is cheap: verified archives are cached in
    <repo>\payload\.cache and are not downloaded again.

.PARAMETER Force
    Delete the cache and the staged payload, then fetch everything again.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# payload\ is zipped verbatim into the application executable at build time, so the download
# cache must live outside it.
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$PayloadDir = Join-Path $RepoRoot 'payload'
$CacheDir   = Join-Path $RepoRoot 'build\payload-cache'

# --------------------------------------------------------------------------------------
# Pinned sources. Digests were verified against the publishers' own checksum listings:
#   Tor     : https://archive.torproject.org/tor-package-archive/torbrowser/15.0.21/sha256sums-unsigned-build.txt
#   sing-box: the "digest" field of the GitHub release asset API response
#   Wintun  : the SHA2-256 published next to the download link on https://www.wintun.net/
# --------------------------------------------------------------------------------------
$Sources = @(
    [pscustomobject]@{
        Name   = 'tor-expert-bundle-windows-x86_64-15.0.21.tar.gz'
        Url    = 'https://archive.torproject.org/tor-package-archive/torbrowser/15.0.21/tor-expert-bundle-windows-x86_64-15.0.21.tar.gz'
        Sha256 = 'f22b8b17cb18c9fa775dfcf68acf6a2fe788336535fe94645204ca85158aa490'
    }
    [pscustomobject]@{
        Name   = 'sing-box-1.14.0-windows-amd64.zip'
        Url    = 'https://github.com/SagerNet/sing-box/releases/download/v1.14.0/sing-box-1.14.0-windows-amd64.zip'
        Sha256 = '3ffb56267da14e287be48bd10cf7e6505260125bad940b75101fbb4d5d58e5d6'
    }
    [pscustomobject]@{
        Name   = 'wintun-0.14.1.zip'
        Url    = 'https://www.wintun.net/builds/wintun-0.14.1.zip'
        Sha256 = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'
    }
)

# Files copied into payload\, keyed by the archive they come from. Everything else in the
# archives (documentation, other architectures, the Conjure transport) is left behind.
$StageMap = @{
    'tor-expert-bundle-windows-x86_64-15.0.21.tar.gz' = @(
        @{ From = 'tor/tor.exe';                              To = 'tor/tor.exe' }
        @{ From = 'tor/pluggable_transports/lyrebird.exe';    To = 'tor/pluggable_transports/lyrebird.exe' }
        @{ From = 'data/geoip';                               To = 'tor/data/geoip' }
        @{ From = 'data/geoip6';                              To = 'tor/data/geoip6' }
    )
    'sing-box-1.14.0-windows-amd64.zip' = @(
        @{ From = 'sing-box-1.14.0-windows-amd64/sing-box.exe'; To = 'sing-box/sing-box.exe' }
        @{ From = 'sing-box-1.14.0-windows-amd64/LICENSE';      To = 'sing-box/LICENSE' }
    )
    'wintun-0.14.1.zip' = @(
        @{ From = 'wintun/bin/amd64/wintun.dll';  To = 'sing-box/wintun.dll' }
        @{ From = 'wintun/LICENSE.txt';           To = 'sing-box/wintun-LICENSE.txt' }
    )
}

function Write-Step([string]$Message) {
    Write-Host "  $Message" -ForegroundColor Cyan
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Resolve-Archive([pscustomobject]$Source) {
    $target = Join-Path $CacheDir $Source.Name

    if (Test-Path $target) {
        if ((Get-Sha256 $target) -eq $Source.Sha256) {
            Write-Step "cached   $($Source.Name)"
            return $target
        }
        Write-Step "stale    $($Source.Name) (digest mismatch, re-downloading)"
        Remove-Item $target -Force
    }

    Write-Step "download $($Source.Name)"
    $tmp = "$target.part"
    try {
        Invoke-WebRequest -Uri $Source.Url -OutFile $tmp -UseBasicParsing
    } catch {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
        throw "Download failed for $($Source.Url): $($_.Exception.Message)"
    }

    $actual = Get-Sha256 $tmp
    if ($actual -ne $Source.Sha256) {
        Remove-Item $tmp -Force
        throw "SHA-256 mismatch for $($Source.Name).`n  expected $($Source.Sha256)`n  actual   $actual"
    }

    Move-Item $tmp $target -Force
    Write-Step "verified $($Source.Name)"
    return $target
}

function Expand-Archive-Any([string]$ArchivePath, [string]$Destination) {
    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
    New-Item -ItemType Directory -Force $Destination | Out-Null

    if ($ArchivePath.EndsWith('.zip')) {
        Expand-Archive -Path $ArchivePath -DestinationPath $Destination -Force
    } elseif ($ArchivePath.EndsWith('.tar.gz')) {
        & tar -xzf $ArchivePath -C $Destination
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE for $ArchivePath" }
    } else {
        throw "Unsupported archive type: $ArchivePath"
    }
}

# --------------------------------------------------------------------------------------

Write-Host ''
Write-Host 'TorVpnForWindows - payload fetch' -ForegroundColor White
Write-Host "  repository : $RepoRoot"
Write-Host "  payload    : $PayloadDir"
Write-Host ''

if ($Force -and (Test-Path $PayloadDir)) {
    Write-Step 'removing existing payload (-Force)'
    Remove-Item $PayloadDir -Recurse -Force
}

New-Item -ItemType Directory -Force $CacheDir | Out-Null

$extractRoot = Join-Path $CacheDir 'extract'
$staged = 0

foreach ($source in $Sources) {
    $archive = Resolve-Archive $source
    $extractDir = Join-Path $extractRoot ([IO.Path]::GetFileNameWithoutExtension($source.Name))
    Expand-Archive-Any -ArchivePath $archive -Destination $extractDir

    foreach ($entry in $StageMap[$source.Name]) {
        $src = Join-Path $extractDir ($entry.From -replace '/', '\')
        $dst = Join-Path $PayloadDir ($entry.To -replace '/', '\')

        if (-not (Test-Path $src)) {
            throw "Archive $($source.Name) does not contain '$($entry.From)'. The upstream layout may have changed."
        }

        New-Item -ItemType Directory -Force (Split-Path $dst -Parent) | Out-Null
        Copy-Item $src $dst -Force
        $staged++
    }
}

Remove-Item $extractRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Staged payload:' -ForegroundColor White
Get-ChildItem -Recurse -File $PayloadDir | ForEach-Object {
    '  {0,10:N0}  {1}' -f $_.Length, $_.FullName.Substring($PayloadDir.Length + 1)
}

Write-Host ''
Write-Host "$staged file(s) ready. You can now build the solution." -ForegroundColor Green
Write-Host ''
