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
    @{ Rx = 'DOMParser'; Why = 'parses a fetched response as HTML' },
    @{ Rx = '\$\([^)]*\)\.load\s*\('; Why = 'jQuery .load() injects fetched HTML' },
    @{ Rx = "dataType\s*:\s*['""]html['""]"; Why = 'requests HTML instead of JSON' },
    @{ Rx = '(?:innerHTML|outerHTML)\s*=\s*await\s+[A-Za-z_$][\w$]*\.text\s*\(\s*\)'; Why = 'puts a fetched response body into HTML' },
    # Dataflow, not line shape: catches `const html = await response.text();` followed several statements/lines
    # later by `node.innerHTML = html` (the lightbox defect that the old same-line regex missed).
    @{ Rx = '(?<body>[A-Za-z_$][\w$]*)\s*=\s*await\s+[A-Za-z_$][\w$]*\.text\s*\(\s*\)\s*;.{0,4000}?(?:(?:innerHTML|outerHTML)\s*=\s*\k<body>\b|insertAdjacentHTML\s*\([^,]+,\s*\k<body>\b)'; Why = 'puts a fetched response body into HTML' }
)

$violations = @()
foreach ($file in Get-ChildItem -Path $jsDir -Filter *.js -File -Recurse) {
    $source = Get-Content $file.FullName -Raw
    foreach ($p in $patterns) {
        foreach ($match in [regex]::Matches(
            $source, $p.Rx,
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
                [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
            $line = ([regex]::Matches($source.Substring(0, $match.Index), "`n")).Count + 1
            $snippet = ($match.Value -replace '\s+', ' ').Trim()
            if ($snippet.Length -gt 180) { $snippet = $snippet.Substring(0, 180) + '…' }
            $violations += "{0}:{1}: {2}`n    {3}" -f $file.Name, $line, $p.Why, $snippet
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
