#Requires -Version 5.1
<#
.SYNOPSIS
    Vendors the TextMate grammars and themes listed in grammars/manifest.json
    into embedded-resource form.

.DESCRIPTION
    Downloads each grammar, validates that it parses as JSON and that its
    scopeName matches the manifest, gzips it, and writes it into the project's
    Resources folder for embedding.

    Themes are flattened first: VS Code's dark_plus.json is a delta over
    dark_vs.json expressed as an "include" key with a relative path. Resolving
    that at runtime would mean shipping a path resolver for a problem we can
    solve once, here.

    Re-run this when you want to pick up upstream grammar fixes. It is not part
    of the build — the .json.gz files are committed.

.EXAMPLE
    ./tools/fetch-grammars.ps1

.EXAMPLE
    ./tools/fetch-grammars.ps1 -Verbose -Force
#>
[CmdletBinding()]
param(
    [string] $ManifestPath = (Join-Path $PSScriptRoot '..\grammars\manifest.json'),
    [string] $OutputRoot   = (Join-Path $PSScriptRoot '..\src\Nib\Resources'),
    [switch] $Force,
    [switch] $NoCompress
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-GzipFile {
    param([Parameter(Mandatory)][byte[]] $Bytes, [Parameter(Mandatory)][string] $Path)

    $out = [IO.File]::Create($Path)
    try {
        $gz = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::Optimal)
        try { $gz.Write($Bytes, 0, $Bytes.Length) } finally { $gz.Dispose() }
    } finally { $out.Dispose() }
}

function Get-RemoteJson {
    param([Parameter(Mandatory)][string] $Uri)
    Write-Verbose "GET $Uri"
    $resp = Invoke-WebRequest -Uri $Uri -UseBasicParsing
    if ($resp.StatusCode -ne 200) { throw "HTTP $($resp.StatusCode) for $Uri" }
    return [Text.Encoding]::UTF8.GetString($resp.Content)
}

function Merge-ThemeChain {
    <#
        VS Code themes form an include chain, base first. Later files override
        earlier ones for 'colors', and their tokenColors are appended (later
        rules win in TextMate theme matching).
    #>
    param([Parameter(Mandatory)][string[]] $Urls)

    $colors      = @{}
    $tokenColors = New-Object Collections.ArrayList
    $name        = $null
    $type        = 'dark'

    foreach ($url in $Urls) {
        $obj = Get-RemoteJson -Uri $url | ConvertFrom-Json
        if ($obj.PSObject.Properties.Name -contains 'name')  { $name = $obj.name }
        if ($obj.PSObject.Properties.Name -contains 'type')  { $type = $obj.type }
        if ($obj.PSObject.Properties.Name -contains 'colors' -and $obj.colors) {
            foreach ($p in $obj.colors.PSObject.Properties) { $colors[$p.Name] = $p.Value }
        }
        if ($obj.PSObject.Properties.Name -contains 'tokenColors' -and $obj.tokenColors) {
            foreach ($tc in $obj.tokenColors) { [void]$tokenColors.Add($tc) }
        }
    }

    return [ordered]@{
        name        = $name
        type        = $type
        colors      = $colors
        tokenColors = $tokenColors
    }
}

# ---------------------------------------------------------------------------

if (-not (Test-Path $ManifestPath)) { throw "Manifest not found: $ManifestPath" }
$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json

$grammarDir = Join-Path $OutputRoot 'Grammars'
$themeDir   = Join-Path $OutputRoot 'Themes'
foreach ($d in @($grammarDir, $themeDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

$ext        = if ($NoCompress) { '.json' } else { '.json.gz' }
$totalRaw   = 0L
$totalOut   = 0L
$failures   = New-Object Collections.ArrayList

Write-Host "Vendoring $($manifest.grammars.Count) grammars and $($manifest.themes.Count) themes" -ForegroundColor Cyan
Write-Host ''

foreach ($g in $manifest.grammars) {
    $dest = Join-Path $grammarDir "$($g.id)$ext"
    if ((Test-Path $dest) -and -not $Force) {
        Write-Host ("  skip   {0,-16} (exists; -Force to refresh)" -f $g.id) -ForegroundColor DarkGray
        $totalOut += (Get-Item $dest).Length
        continue
    }

    try {
        $text = Get-RemoteJson -Uri $g.url
        $obj  = $text | ConvertFrom-Json

        if ($obj.scopeName -ne $g.scopeName) {
            throw "scopeName drift: manifest says '$($g.scopeName)', upstream says '$($obj.scopeName)'"
        }

        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
        $totalRaw += $bytes.Length

        if ($NoCompress) { [IO.File]::WriteAllBytes($dest, $bytes) }
        else             { Write-GzipFile -Bytes $bytes -Path $dest }

        $outLen = (Get-Item $dest).Length
        $totalOut += $outLen
        Write-Host ("  ok     {0,-16} {1,-26} {2,7:N0} -> {3,6:N0} bytes" -f `
                    $g.id, $g.scopeName, $bytes.Length, $outLen) -ForegroundColor Green
    }
    catch {
        Write-Host ("  FAIL   {0,-16} {1}" -f $g.id, $_.Exception.Message) -ForegroundColor Red
        [void]$failures.Add($g.id)
    }
}

Write-Host ''

foreach ($t in $manifest.themes) {
    $dest = Join-Path $themeDir "$($t.id)$ext"
    if ((Test-Path $dest) -and -not $Force) {
        Write-Host ("  skip   {0,-16} (exists; -Force to refresh)" -f $t.id) -ForegroundColor DarkGray
        continue
    }

    try {
        # Base files first, then the delta that overrides them.
        $chain = @()
        if ($t.includes) { $chain += $t.includes }
        $chain += $t.url

        $merged = Merge-ThemeChain -Urls $chain
        $json   = $merged | ConvertTo-Json -Depth 12 -Compress
        $bytes  = [Text.Encoding]::UTF8.GetBytes($json)

        if ($NoCompress) { [IO.File]::WriteAllBytes($dest, $bytes) }
        else             { Write-GzipFile -Bytes $bytes -Path $dest }

        $outLen = (Get-Item $dest).Length
        $totalOut += $outLen
        Write-Host ("  ok     {0,-16} {1,-26} {2,7:N0} -> {3,6:N0} bytes  ({4} rules)" -f `
                    $t.id, $t.label, $bytes.Length, $outLen, $merged.tokenColors.Count) -ForegroundColor Green
    }
    catch {
        Write-Host ("  FAIL   {0,-16} {1}" -f $t.id, $_.Exception.Message) -ForegroundColor Red
        [void]$failures.Add($t.id)
    }
}

Write-Host ''
Write-Host ("Embedded payload: {0:N0} KB" -f ($totalOut / 1KB)) -ForegroundColor Cyan
if ($totalRaw -gt 0) {
    Write-Host ("Compression:      {0:N0} KB raw -> {1:P0}" -f ($totalRaw / 1KB), ($totalOut / $totalRaw))
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Failed: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host 'Done.' -ForegroundColor Green
