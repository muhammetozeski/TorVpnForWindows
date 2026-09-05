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

Write-Host ''
Write-Host 'Published:' -ForegroundColor White
Get-ChildItem $PublishDir -File | ForEach-Object {
    '  {0,8:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name
}
Write-Host ''
