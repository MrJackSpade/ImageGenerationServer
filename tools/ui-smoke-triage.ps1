#requires -Version 7.0
<#
  ui-smoke-triage.ps1
  --------------------------------------------------------------------------
  Turns a ui-smoke.ps1 report into a work list: the same failure hit by nine workflows is ONE thing to
  fix, and the point of this is to say which one.

  A run produces a result per configuration. Read as a list that is 147 lines of mostly the same
  sentence; grouped by what actually went wrong it is usually a handful of causes. So failures are
  bucketed by CAUSE -- the error text with the run-specific parts (ids, paths, numbers) removed, so two
  workflows that died the same way land together -- and each bucket names every configuration in it.

  Classification is by evidence in the error, and anything that does not match a known shape is
  reported as unclassified rather than filed under a guess. An unclassified bucket is a real answer: it
  means a failure mode nobody has seen before, which is the most interesting thing a run can produce.

  Usage:
      .\tools\ui-smoke-triage.ps1
      .\tools\ui-smoke-triage.ps1 -ReportPath .\ui-smoke-report.json
#>
[CmdletBinding()]
param(
    [string] $ReportPath = 'ui-smoke-report.json',
    [switch] $Verbose_
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ReportPath)) { throw "No report at $ReportPath. Run tools\ui-smoke.ps1 first." }
$report = Get-Content $ReportPath -Raw | ConvertFrom-Json

function Head ($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }

<#
  What a failure IS, decided from the error text. Ordered most specific first: an out-of-memory that
  also mentions a node name is an OOM, not a node problem.
#>
$Shapes = [ordered]@{
    'out of VRAM'            = 'out of memory|OutOfMemoryError|CUDA out of memory|allocate .* GiB'
    'node not installed'     = 'not registered|unknown node|does not exist.*node|Node type .* not found|has no attribute .*Node'
    'model file missing'     = 'not offered for this slot|no such file|does not exist|FileNotFoundError|not in list|value not in list'
    'graph rejected'         = 'invalid prompt|required input is missing|Return type mismatch|does not match input type'
    'size refused'           = 'resolution|width|height|must be a multiple|envelope|outside the'
    'renderer unreachable'   = 'refused|unreachable|502|Bad Gateway|connection'
    'prompt rejected'        = 'prompt|negative|instruction|required prefix'
    'timed out or cancelled' = 'cancelled|interrupted'
}

function Get-Shape {
    param([string] $Text)
    if (-not $Text) { return 'no error text' }
    foreach ($name in $Shapes.Keys) {
        if ($Text -match $Shapes[$name]) { return $name }
    }
    return 'UNCLASSIFIED'
}

<#
  The run-specific parts stripped out, so the same fault reported against four different files groups as
  one. Filenames, ids, sizes and numbers are exactly what differs between two instances of one cause.
#>
function Get-Signature {
    param([string] $Text)
    if (-not $Text) { return '(none)' }
    $s = $Text
    $s = $s -replace "'[^']*'", "'X'"
    $s = $s -replace '"[^"]*"', '"X"'
    $s = $s -replace '[A-Za-z]:\\[^\s,;]+', 'PATH'
    $s = $s -replace '\b[\w.-]+\.(safetensors|gguf|ckpt|pth|pt|bin)\b', 'FILE'
    $s = $s -replace '\b\d+(\.\d+)?\b', 'N'
    $s = $s -replace '\s+', ' '
    return $s.Trim()
}

# --- bindings -------------------------------------------------------------------------------------

Head "Bindings"
$b = $report.binding
if ($b) {
    Write-Host ("   {0} bound, {1} node slot(s) satisfied, {2} failed, {3} attempted" -f
        $b.Bound, $b.Nodes, @($b.Failed).Count, $b.Attempted)

    $byShape = @($b.Failed) | Where-Object { $_ } | Group-Object { Get-Shape $_ } | Sort-Object Count -Descending
    foreach ($g in $byShape) {
        Write-Host ""
        Write-Host ("   {0}  ({1})" -f $g.Name, $g.Count) -ForegroundColor Yellow
        foreach ($f in $g.Group) { Write-Host "     $f" }
    }
}
else { Write-Host "   (binding was skipped)" }

# --- renders --------------------------------------------------------------------------------------

$results = @($report.results)
$byStatus = $results | Group-Object Status | Sort-Object Count -Descending

Head "Renders"
foreach ($g in $byStatus) {
    $colour = switch ($g.Name) { 'done' { 'Green' } 'skipped' { 'DarkGray' } 'unavailable' { 'DarkGray' } default { 'Red' } }
    Write-Host ("   {0,-14} {1}" -f $g.Name, $g.Count) -ForegroundColor $colour
}

$failures = @($results | Where-Object { $_.Status -notin 'done', 'skipped', 'unavailable' })
if (-not $failures.Count) {
    Head "Nothing to triage"
    return
}

<#
  The work list. Buckets are ordered by how many configurations each blocks, because that is the order
  they are worth fixing in -- one cause holding up nine workflows outranks three causes holding up one
  each.
#>
Head "Causes, worst first"
$buckets = $failures |
    Group-Object { "{0}`u{241}{1}" -f (Get-Shape $_.Error), (Get-Signature $_.Error) } |
    Sort-Object Count -Descending

$n = 0
foreach ($bucket in $buckets) {
    $n++
    $shape, $sig = $bucket.Name -split "`u{241}", 2
    $colour = if ($shape -eq 'UNCLASSIFIED') { 'Magenta' } else { 'Yellow' }

    Write-Host ""
    Write-Host ("   [{0}] {1} - blocks {2} configuration(s)" -f $n, $shape, $bucket.Count) -ForegroundColor $colour
    Write-Host ("        {0}" -f $sig) -ForegroundColor DarkGray
    foreach ($f in $bucket.Group) {
        Write-Host ("        {0,-34} {1}" -f $f.Id, $f.Error)
    }
}

Head "Summary"
Write-Host ("   {0} failure(s) in {1} cause(s)" -f $failures.Count, $buckets.Count)
$unclassified = @($buckets | Where-Object { $_.Name -like 'UNCLASSIFIED*' })
if ($unclassified.Count) {
    Write-Host ("   {0} cause(s) match no known shape - look at these first" -f $unclassified.Count) -ForegroundColor Magenta
}
