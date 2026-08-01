# check-js-json-only.ps1 — fail the build if first-party client JS consumes HTML instead of JSON.
#
# Client JS talks to the server by calling JSON endpoints and building the DOM itself. It never fetches a page or
# partial and feeds the response to the DOM. This is enforced rather than trusted because the failure spreads: a
# whole-page HTML poll written once was copied across six pages, re-rendering entire Razor views every few seconds
# to patch a status badge.
#
#   pwsh tools/check-js-json-only.ps1
#
# Building an element's innerHTML from a string YOU assembled out of parsed JSON is correct and is not flagged.
# Feeding a FETCHED response into innerHTML/DOMParser is the defect.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$jsDir = Join-Path $root 'src/ImageGen.Web/wwwroot/js'

$patterns = @(
    @{ Rx = 'fetch\s*\(\s*(location|window\.location)'; Why = 'fetches its own page as data' },
    @{ Rx = 'DOMParser';                                Why = 'parses a fetched response as HTML' },
    @{ Rx = '\$\([^)]*\)\.load\s*\(';                   Why = 'jQuery .load() injects fetched HTML' },
    @{ Rx = "dataType\s*:\s*['""]html['""]";            Why = 'requests HTML instead of JSON' },
    @{ Rx = '\.text\(\)[^\n]*innerHTML';                Why = 'puts a fetched response body into innerHTML' }
)

$violations = @()
foreach ($file in Get-ChildItem -Path $jsDir -Filter *.js -File -Recurse) {
    $lines = Get-Content $file.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($p in $patterns) {
            if ($lines[$i] -match $p.Rx) {
                $violations += "{0}:{1}: {2}`n    {3}" -f $file.Name, ($i + 1), $p.Why, $lines[$i].Trim()
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host 'Client JS must consume JSON, not HTML:' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'The endpoint is wrong, not the JS — make it return JSON and build the DOM from that.'
    exit 1
}

Write-Host "OK: $((Get-ChildItem -Path $jsDir -Filter *.js -File -Recurse).Count) JS files consume JSON only."
