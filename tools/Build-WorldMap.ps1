#Requires -Version 7.0
<#
.SYNOPSIS
    Turns Natural Earth's simplified land outlines into a single vector path for the status map.

.DESCRIPTION
    Downloads ne_110m_land, the coarsest Natural Earth land layer, and projects it with a plain
    equirectangular mapping into a 720 x 360 box. That projection is used because placing a marker
    on it is arithmetic rather than a projection library: x is longitude and y is latitude, scaled.

    Antarctica is dropped. It occupies a quarter of the height of an equirectangular map, no Tor exit
    relay is there, and leaving it in pushes everything else into a thin strip.

    The result is written as path data next to the application's other assets and embedded at build
    time. Run this only when the map itself should change; the output is committed.
#>
[CmdletBinding()]
param(
    [int]$Width = 720,
    [int]$Height = 360,
    [string]$Source = 'https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_110m_land.geojson'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Output   = Join-Path $RepoRoot 'src\TorVpnForWindows\Assets\world-map.path'

Write-Host "  downloading $Source" -ForegroundColor Cyan
$json = (Invoke-WebRequest -Uri $Source -UseBasicParsing -TimeoutSec 60).Content
$geo = $json | ConvertFrom-Json

$builder = [System.Text.StringBuilder]::new()
$rings = 0
$points = 0

function Add-Ring($ring) {
    # A ring below this latitude is Antarctic; see the note above.
    $maxLat = ($ring | ForEach-Object { $_[1] } | Measure-Object -Maximum).Maximum
    if ($maxLat -lt -60) { return }

    $first = $true
    foreach ($point in $ring) {
        $lon = [double]$point[0]
        $lat = [double]$point[1]

        $x = ($lon + 180.0) / 360.0 * $script:Width
        $y = (90.0 - $lat) / 180.0 * $script:Height

        # One decimal is under a tenth of a pixel at this size and keeps the string small.
        #
        # Formatted with the invariant culture on purpose. Under a culture that uses a comma as the
        # decimal separator, "224.5" comes out as "224,5" and the comma is also what separates the
        # two coordinates, so the path data becomes unparseable nonsense.
        $pair = [string]::Format([cultureinfo]::InvariantCulture, '{0:0.#},{1:0.#}', $x, $y)

        if ($first) {
            [void]$script:builder.Append('M').Append($pair)
            $first = $false
        } else {
            [void]$script:builder.Append('L').Append($pair)
        }

        $script:points++
    }

    [void]$script:builder.Append('Z')
    $script:rings++
}

foreach ($feature in $geo.features) {
    $geometry = $feature.geometry

    switch ($geometry.type) {
        'Polygon' {
            foreach ($ring in $geometry.coordinates) { Add-Ring $ring }
        }
        'MultiPolygon' {
            foreach ($polygon in $geometry.coordinates) {
                foreach ($ring in $polygon) { Add-Ring $ring }
            }
        }
        default {
            Write-Host "  skipping geometry type $($geometry.type)" -ForegroundColor Yellow
        }
    }
}

$data = $builder.ToString()
Set-Content -Path $Output -Value $data -Encoding utf8 -NoNewline

Write-Host ''
Write-Host "  rings  : $rings"
Write-Host "  points : $points"
Write-Host "  size   : $([math]::Round($data.Length / 1KB, 1)) KB"
Write-Host "  written: $Output" -ForegroundColor Green
