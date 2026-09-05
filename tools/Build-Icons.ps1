#Requires -Version 7.0
<#
.SYNOPSIS
    Turns the generated icon artwork into transparent PNGs and a multi-size ICO.

.DESCRIPTION
    The artwork is produced on a plain white background. The background is removed by flood filling
    inward from each corner rather than by making every white pixel transparent, because the drawings
    contain white areas of their own: the bulb of the spring onion and the highlight strokes on the
    onion. Those are enclosed by outlines, so a flood fill cannot reach them.

    Produces, for each state, a trimmed and padded square PNG, and an ICO carrying every size
    Windows asks for so the icon never appears as a scaled-up 16 pixel image.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$AssetsDir = Join-Path $RepoRoot 'src\TorVpnForWindows\Assets'

$magick = (Get-Command magick -ErrorAction SilentlyContinue)?.Source
if (-not $magick) {
    throw 'ImageMagick (magick) was not found on PATH.'
}

$states = 'disconnected', 'connecting', 'connected'
$size = 256

foreach ($state in $states) {
    $source = Get-ChildItem -Path $AssetsDir -Filter "raw-$state.*" -File |
        Where-Object { $_.Extension -in '.jpg', '.png' } |
        Select-Object -First 1

    if (-not $source) {
        throw "Artwork for '$state' was not found in $AssetsDir (expected raw-$state.jpg or .png)."
    }

    $png = Join-Path $AssetsDir "icon-$state.png"
    $ico = Join-Path $AssetsDir "icon-$state.ico"

    # Read the real dimensions so the corner coordinates are right whatever the model produced.
    $dimensions = & $magick identify -format '%w %h' $source.FullName
    $parts = $dimensions -split ' '
    $width = [int]$parts[0]
    $height = [int]$parts[1]
    $right = $width - 1
    $bottom = $height - 1

    Write-Host "  $state : $($source.Name) ($width x $height)" -ForegroundColor Cyan

    & $magick $source.FullName `
        -alpha set `
        -fuzz 18% `
        -fill none -floodfill +0+0 white `
        -fill none -floodfill "+$right+0" white `
        -fill none -floodfill "+0+$bottom" white `
        -fill none -floodfill "+$right+$bottom" white `
        -trim +repage `
        -resize "${size}x${size}" `
        -background none -gravity center -extent "${size}x${size}" `
        $png

    if ($LASTEXITCODE -ne 0) { throw "Background removal failed for $state." }

    # Every size Windows may ask for, so the shell never has to scale a small bitmap up.
    & $magick $png -define icon:auto-resize=256,128,96,64,48,32,24,16 $ico
    if ($LASTEXITCODE -ne 0) { throw "ICO conversion failed for $state." }

    $opaque = & $magick $png -format '%[opaque]' info:
    Write-Host "      transparent: $(if ($opaque -eq 'false') { 'yes' } else { 'NO - the background was not removed' })"
}

# The connected onion is the application's own identity.
Copy-Item (Join-Path $AssetsDir 'icon-connected.ico') (Join-Path $AssetsDir 'icon.ico') -Force
Copy-Item (Join-Path $AssetsDir 'icon-connected.png') (Join-Path $AssetsDir 'icon.png') -Force

Write-Host ''
Get-ChildItem $AssetsDir -Filter 'icon*' | ForEach-Object {
    '  {0,9:N0}  {1}' -f $_.Length, $_.Name
}
