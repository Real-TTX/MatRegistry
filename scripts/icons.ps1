#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Draws the PNG icons of MatRegistry from the SVG logo (scripts/logo.js writes them) with Edge headless: the PWA icons (192, 512, maskable 512),
    the apple-touch-icon (180), the favicon (32) and docs/logo/matregistry-mark-512.png.
.EXAMPLE
    node scripts/logo.js; ./scripts/icons.ps1
#>
$root = Split-Path -Parent $PSScriptRoot
$logo = Join-Path $root 'src/MatRegistry/wwwroot/img/logo'
$img = Join-Path $root 'src/MatRegistry/wwwroot/img'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge was not found.' }

$work = Join-Path ([IO.Path]::GetTempPath()) "matregistry-icons-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Force $work | Out-Null

function Draw([string]$svg, [int]$size, [string]$target) {
    # an html page that shows the svg at exactly the size of the window, without a margin
    $html = Join-Path $work "icon-$size.html"
    $source = (Get-Content -Raw (Join-Path $logo $svg)) -replace '<svg ', "<svg width=`"$size`" height=`"$size`" "
    Set-Content -Encoding utf8 $html "<!doctype html><html><head><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}svg{display:block}</style></head><body>$source</body></html>"
    & $edge --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 "--window-size=$size,$size" "--screenshot=$target" "file:///$($html -replace '\\','/')" 2>$null | Out-Null
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $target) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $target)) { throw "Edge did not draw $target" }
    Write-Host "drew $target"
}

try {
    Draw 'matregistry-mark.svg' 192 (Join-Path $img 'icon-192.png')
    Draw 'matregistry-mark.svg' 512 (Join-Path $img 'icon-512.png')
    Draw 'matregistry-maskable.svg' 512 (Join-Path $img 'icon-maskable-512.png')
    Draw 'matregistry-maskable.svg' 180 (Join-Path $img 'apple-touch-icon.png')
    Draw 'matregistry-favicon.svg' 32 (Join-Path $img 'favicon-32.png')
    Copy-Item (Join-Path $img 'icon-512.png') (Join-Path $root 'docs/logo/matregistry-mark-512.png') -Force
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
