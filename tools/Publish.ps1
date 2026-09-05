#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes both executables, signs them and verifies the signatures.

.DESCRIPTION
    Produces two single-file builds in <repo>\publish:

      TorVpnForWindows.exe                                    portable, no runtime needed
      TorVpnForWindows-FrameworkDependent-RequiresNET10.exe   needs the .NET 10 desktop runtime

    Signing happens before the files are copied anywhere else, so nothing unsigned escapes the
    publish folder. A running copy of the program cannot be signed, so close it first.

.PARAMETER SkipSigning
    Produce the executables without signing them. Useful when only checking that the build works.
#>
[CmdletBinding()]
param(
    [switch]$SkipSigning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Project    = Join-Path $RepoRoot 'src\TorVpnForWindows\TorVpnForWindows.csproj'
$PublishDir = Join-Path $RepoRoot 'publish'
$SigningDir = 'C:\E\kp\aaBenimProgramlarim\Imza'

function Write-Step([string]$Message) {
    Write-Host "  $Message" -ForegroundColor Cyan
}

Write-Host ''
Write-Host 'TorVpnForWindows - publish' -ForegroundColor White
Write-Host ''

if (-not (Test-Path (Join-Path $RepoRoot 'payload\sing-box\sing-box.exe'))) {
    throw "Payload is missing. Run 'pwsh tools\Fetch-Payload.ps1' first."
}

$running = Get-Process -Name 'TorVpnForWindows' -ErrorAction SilentlyContinue
if ($running) {
    throw "TorVpnForWindows is running (PID $($running.Id -join ', ')). Close it before publishing; a running executable cannot be signed."
}

if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}
New-Item -ItemType Directory -Force $PublishDir | Out-Null

$staging = Join-Path $RepoRoot 'build\publish-staging'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

# --- portable, self-contained -----------------------------------------------------------------
Write-Step 'publishing the portable build'
$portableOut = Join-Path $staging 'portable'
dotnet publish $Project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o $portableOut --nologo
if ($LASTEXITCODE -ne 0) { throw "The portable publish failed with exit code $LASTEXITCODE." }

Copy-Item (Join-Path $portableOut 'TorVpnForWindows.exe') `
          (Join-Path $PublishDir 'TorVpnForWindows.exe') -Force

# --- framework dependent ----------------------------------------------------------------------
Write-Step 'publishing the framework dependent build'
$fdOut = Join-Path $staging 'framework-dependent'
dotnet publish $Project -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o $fdOut --nologo
if ($LASTEXITCODE -ne 0) { throw "The framework dependent publish failed with exit code $LASTEXITCODE." }

Copy-Item (Join-Path $fdOut 'TorVpnForWindows.exe') `
          (Join-Path $PublishDir 'TorVpnForWindows-FrameworkDependent-RequiresNET10.exe') -Force

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

# --- signing ----------------------------------------------------------------------------------
if ($SkipSigning) {
    Write-Step 'skipping signing (-SkipSigning)'
} else {
    $signScript   = Join-Path $SigningDir 'Imzala.ps1'
    $verifyScript = Join-Path $SigningDir 'Dogrula.ps1'

    if (-not (Test-Path $signScript)) {
        throw "The signing script was not found at $signScript."
    }

    Write-Step 'signing'
    & $signScript $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "Signing failed with exit code $LASTEXITCODE." }

    Write-Step 'verifying the signatures'
    & $verifyScript $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed with exit code $LASTEXITCODE." }
}

# --- bundle with the helper programs ------------------------------------------------------------
# The executable already contains these, so the archive is not needed to run the program. It exists
# so the third-party binaries can be inspected without unpacking the executable, and so the folder
# can be put on PATH, which makes the application prefer these copies over its embedded ones.
Write-Step 'building the archive with the helper programs'

$bundleRoot = Join-Path $RepoRoot 'build\bundle'
if (Test-Path $bundleRoot) { Remove-Item $bundleRoot -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $bundleRoot 'bin') | Out-Null

Copy-Item (Join-Path $PublishDir 'TorVpnForWindows.exe') $bundleRoot -Force
Copy-Item (Join-Path $RepoRoot 'README.md') $bundleRoot -Force
Copy-Item (Join-Path $RepoRoot 'LICENSE') $bundleRoot -Force

$payload = Join-Path $RepoRoot 'payload'
Copy-Item (Join-Path $payload 'tor\tor.exe')                              (Join-Path $bundleRoot 'bin') -Force
Copy-Item (Join-Path $payload 'tor\pluggable_transports\lyrebird.exe')    (Join-Path $bundleRoot 'bin') -Force
Copy-Item (Join-Path $payload 'sing-box\sing-box.exe')                    (Join-Path $bundleRoot 'bin') -Force
Copy-Item (Join-Path $payload 'sing-box\wintun.dll')                      (Join-Path $bundleRoot 'bin') -Force
Copy-Item (Join-Path $payload 'sing-box\LICENSE')                         (Join-Path $bundleRoot 'bin\sing-box-LICENSE.txt') -Force
Copy-Item (Join-Path $payload 'sing-box\wintun-LICENSE.txt')              (Join-Path $bundleRoot 'bin') -Force
Copy-Item (Join-Path $payload 'tor\data\geoip')                           (Join-Path $bundleRoot 'bin') -Force
Copy-Item (Join-Path $payload 'tor\data\geoip6')                          (Join-Path $bundleRoot 'bin') -Force

@'
Tor VPN for Windows - portable bundle

Run TorVpnForWindows.exe. It asks for administrator rights, which it needs to create the network
adapter, install routes and apply the kill switch filters.

The bin folder holds the third-party programs the application uses:

  tor.exe        the Tor client
  lyrebird.exe   the pluggable transport used for obfs4, Snowflake and meek bridges
  sing-box.exe   creates the network adapter and does the routing
  wintun.dll     the adapter driver sing-box loads
  geoip, geoip6  the databases Tor uses for exit country selection

You do not need them to run the program: identical copies are already inside the executable and are
unpacked on first run. They are here so they can be inspected, checked against the publishers'
own releases, or kept up to date independently.

To use these copies instead of the embedded ones, add this bin folder to your PATH. The application
looks there first and falls back to its own copies, so a version you keep updated wins.

Sources and versions are listed in README.md.
'@ | Set-Content (Join-Path $bundleRoot 'OKUBENI-READ-ME-FIRST.txt') -Encoding utf8

$bundleZip = Join-Path $PublishDir 'TorVpnForWindows-portable-with-tools.zip'
Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $bundleZip -Force

Remove-Item $bundleRoot -Recurse -Force

# --- signature trust files ------------------------------------------------------------------------
# Shipped with every release so Windows can be told to trust the signing certificate. Built here
# rather than by hand, because the publish folder is emptied at the start of every run.
if (-not $SkipSigning) {
    $trustSource = Join-Path $SigningDir 'Dagitim'

    if (Test-Path $trustSource) {
        Write-Step 'packaging the signature trust files'
        Compress-Archive -Path (Join-Path $trustSource '*') `
                         -DestinationPath (Join-Path $PublishDir 'SignatureTrust.zip') -Force
    } else {
        Write-Host "  the signature trust folder was not found at $trustSource" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host 'Published:' -ForegroundColor White
Get-ChildItem $PublishDir -File | ForEach-Object {
    '  {0,8:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name
}

# --- copy to the local programs folder ------------------------------------------------------------
$LocalPrograms = 'C:\E\kp\aaBenimProgramlarim\TorVpnForWindows'

try {
    New-Item -ItemType Directory -Force $LocalPrograms | Out-Null
    Copy-Item (Join-Path $PublishDir 'TorVpnForWindows.exe') $LocalPrograms -Force
    Write-Host ''
    Write-Host "Copied the portable build to $LocalPrograms" -ForegroundColor Green
} catch {
    Write-Host ''
    Write-Host "Could not copy to ${LocalPrograms}: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ''
