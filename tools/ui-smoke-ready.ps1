#requires -Version 7.0
<#
  ui-smoke-ready.ps1
  --------------------------------------------------------------------------
  Which configurations can be tested RIGHT NOW, decided from the weights on disk rather than by
  reading names.

  "Ready" from the catalogue is not enough while a bulk download is in flight: a slot counts as
  satisfied the moment ComfyUI can see a file, and a half-written file is visible from its first byte.
  So a configuration can read ready and still fail in a loader with a shape error that looks like a bug
  and is not one.

  This walks each configuration's requirements to its bound filename and asks
  tools/check-model-integrity.ps1 whether that file is whole. A configuration is testable only when
  every file it needs is complete. Getting this wrong is not free -- it wastes a render and then files a
  ticket against a model that was simply still arriving.

  Emits a comma-joined id list suitable for `ui-smoke.ps1 -Only`, and names what each blocked
  configuration is waiting on.

  Usage:
      .\tools\ui-smoke-ready.ps1
      .\tools\ui-smoke-ready.ps1 -Exclude ltx2-i2v,pixelize-sd35     # skip known-failing ones
#>
[CmdletBinding()]
param(
    [string]   $BaseUrl = 'http://localhost:8080',
    [string[]] $Exclude,
    [string]   $SmokeScript = "$PSScriptRoot\ui-smoke.ps1"
)

$ErrorActionPreference = 'Stop'
$Root = $BaseUrl.TrimEnd('/')

# --- what is incomplete right now ---------------------------------------------------------------
$integrity = & pwsh -NoProfile -File "$PSScriptRoot\check-model-integrity.ps1" 2>&1
$incomplete = @($integrity |
    Select-String 'TRUNCATED\s+(.+)$|UNREADABLE\s+(.+)$|BROKEN\s+(.+)$|OVERLONG\s+(.+)$' |
    ForEach-Object { Split-Path (($_.Matches.Groups[1..4] | Where-Object Value | Select-Object -First 1).Value.Trim()) -Leaf })

# --- what each slot is bound to, per the test's own Bind lines ------------------------------------
$smoke = Get-Content $SmokeScript -Raw
$fileOfSlot = @{}
foreach ($m in [regex]::Matches($smoke, "(?m)^Bind\s+'([^']+)'\s+'([^']+)'")) {
    $fileOfSlot[$m.Groups[1].Value] = $m.Groups[2].Value
}

# --- the catalogue ---------------------------------------------------------------------------------
$null = Invoke-WebRequest "$Root/account/register" -SessionVariable s
$page = Invoke-WebRequest "$Root/account/register" -WebSession $s
$tok = ($page.Content | Select-String 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Matches.Groups[1].Value
$acct = "ready-" + (Get-Date -Format 'HHmmss')
$null = Invoke-WebRequest "$Root/account/register" -Method Post -WebSession $s -MaximumRedirection 5 -Body @{
    __RequestVerificationToken = $tok; username = $acct; password = [guid]::NewGuid().ToString('N')
    displayName = $acct; code = ''; returnUrl = '/'
}
$status = (Invoke-WebRequest "$Root/forge/catalog/status" -WebSession $s).Content | ConvertFrom-Json

$testable = @(); $blocked = @()
foreach ($w in @($status.workflows | Where-Object ready)) {
    $id = [string]$w.id
    if ($Exclude -and ($Exclude -split ',' | ForEach-Object { $_.Trim() }) -contains $id) { continue }

    # A slot with no Bind line is a node slot or one this test does not drive; the catalogue already
    # judged it satisfied, so it is not a reason to hold the configuration back.
    $waiting = @()
    foreach ($slot in @($w.requiredSlots)) {
        $file = $fileOfSlot[[string]$slot]
        if ($file -and $incomplete -contains $file) { $waiting += "$slot ($file)" }
    }
    if ($waiting.Count) { $blocked += [pscustomobject]@{ Id = $id; Waiting = $waiting } }
    else                { $testable += $id }
}

Write-Host ("testable now : {0}" -f $testable.Count) -ForegroundColor Green
Write-Host ("blocked      : {0}  (a required weight is still arriving)" -f $blocked.Count) -ForegroundColor DarkGray
Write-Host ""
foreach ($b in $blocked | Sort-Object Id) {
    Write-Host ("   {0,-34} waiting on {1}" -f $b.Id, ($b.Waiting -join '; ')) -ForegroundColor DarkGray
}
Write-Host ""
($testable | Sort-Object) -join ','
