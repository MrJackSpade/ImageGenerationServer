#requires -Version 7.0
<#
  ui-smoke.ps1
  --------------------------------------------------------------------------
  Drives a LIVE instance the way a browser does: signs in against the real login form, binds every
  catalogue slot the models page would bind, then runs every workflow the library lists and reports
  what each one did.

  It talks to nothing but the endpoints the pages themselves call -- cookie auth from /account/login,
  /forge/catalog/*, /forge/generate, /forge/edit, /forge/upload, /forge/result. No per-user API key
  (that is the MCP's door, not the browser's), and never ComfyUI directly.

  IT DOES NOT STOP ON FAILURE. A workflow that refuses, errors, or never produces an image is recorded
  and the run moves to the next one; the point is one pass that catalogues everything wrong, not a
  bisect that halts on the first thing. The report at the end is the work list.

  It takes no credentials. Every run registers its own throwaway account through the register form, so
  a run starts from an empty history, generates into nobody's library, and cannot depend on what a
  previous run left behind.

  It also takes no prompt, no aspect and no instruction. There is no single value those could hold:
  SeedVR2 has no text conditioning at all, an inpaint takes a full booru-tag prompt describing the
  whole picture while a Kontext edit takes an instruction describing a change, and every workflow
  declares its own resolution envelope. Each one is asked what it wants -- /forge/prompting carries the
  format, the required prefix, whether a negative is supported and the workflow's own example -- which
  is the same metadata the composer builds its controls from.

  Usage:
      .\tools\ui-smoke.ps1
      .\tools\ui-smoke.ps1 -Only chroma1-hd,anima-inpaint
      .\tools\ui-smoke.ps1 -SkipBinding

  There is no per-render deadline, and no option to set one. A render takes as long as it takes; a
  clock here would kill work that was about to succeed and file it as a failure that never happened.
  A job ends when the app says it ended.
#>
[CmdletBinding()]
param(
    [string]   $BaseUrl  = 'http://localhost:8080',

    # Only needed where Auth:RegistrationCode is set. It is the door's requirement, not a knob.
    [string]   $RegistrationCode = '',

    [string[]] $Only,                 # run only these configuration ids
    [switch]   $SkipBinding,          # leave model bindings exactly as they are

    # Between configurations, ask the app to flush ComfyUI's VRAM (POST /forge/free-vram). On by default:
    # without it the renderer keeps the previous config's model resident and the NEXT config can OOM purely
    # from that leftover -- cascade contamination that reads as a per-config failure. It sank six OOM tickets
    # that render fine in isolation. Pass -NoFreeVram to keep the old, faster, contaminating behaviour.
    [switch]   $NoFreeVram,

    # The UI polls its own queue at 2s; this matches rather than inventing a new cadence.
    [int]      $PollSeconds  = 2,

    [string]   $ReportPath = "ui-smoke-report.json"
)

$ErrorActionPreference = 'Stop'
$script:Session = $null
$script:Root    = $BaseUrl.TrimEnd('/')

# --- plumbing ---------------------------------------------------------------------------------

function Write-Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "   $text" -ForegroundColor Green }
function Write-Bad($text)  { Write-Host "   $text" -ForegroundColor Red }
function Write-Meh($text)  { Write-Host "   $text" -ForegroundColor DarkGray }

<#
  Every call goes through here so a failure is DATA, not an exception: an endpoint answering 400 or 502
  is a result this run wants to record, and letting it throw would end the pass at the first bad
  workflow -- exactly what this script exists not to do.
#>
function Invoke-Api {
    param(
        [Parameter(Mandatory)][string] $Path,
        [string] $Method = 'GET',
        $Body
    )
    # Not $args: that is an automatic variable, and shadowing it inside a function is a trap for whoever
    # edits this next.
    $req = @{ Uri = "$script:Root$Path"; Method = $Method; WebSession = $script:Session; ErrorAction = 'Stop' }
    if ($PSBoundParameters.ContainsKey('Body') -and $null -ne $Body) {
        $req.Body        = ($Body | ConvertTo-Json -Depth 12 -Compress)
        $req.ContentType = 'application/json'
    }

    try {
        $r = Invoke-WebRequest @req
        $parsed = $null
        if ($r.Content) { try { $parsed = $r.Content | ConvertFrom-Json } catch { $parsed = $r.Content } }
        return [pscustomobject]@{ Ok = $true; Status = [int]$r.StatusCode; Data = $parsed; Error = $null }
    }
    catch {
        $resp   = $_.Exception.Response
        $status = if ($resp) { [int]$resp.StatusCode } else { 0 }
        $text   = $null
        try { $text = $_.ErrorDetails.Message } catch { }
        if (-not $text -and $resp) { try { $text = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } catch { } }
        $message = $text
        # The app answers failures as { error }; surface that rather than "400 Bad Request".
        if ($text) { try { $j = $text | ConvertFrom-Json; if ($j.error) { $message = $j.error } } catch { } }
        if (-not $message) { $message = $_.Exception.Message }
        return [pscustomobject]@{ Ok = $false; Status = $status; Data = $null; Error = $message }
    }
}

<#
  Flush ComfyUI's VRAM between configurations. The app does NOT free between prompts on its own, so the
  renderer keeps the last config's model resident and the next config can OOM on that leftover alone -- which
  is how six OOM tickets were filed for configurations that render fine from a clean renderer. /forge/free-vram
  is the app's own door to ComfyUI's /free (ComfyUI refuses /free from anyone else). A failure here is warned,
  not fatal: the pass continues, but the next result may be contaminated, so it must be visible.
#>
function Clear-Vram {
    if ($NoFreeVram) { return }
    $f = Invoke-Api '/forge/free-vram' -Method Post
    if (-not $f.Ok) { Write-Bad "free-vram failed ($($f.Error)) — the next configuration may be contaminated" }
}

# --- sign in ----------------------------------------------------------------------------------

<#
  The real form, including its antiforgery token, because that is the door the browser uses. A per-user
  API key would be easier and would test a path no page takes.
#>
function Get-AntiForgeryToken {
    param([Parameter(Mandatory)][string] $Page)
    $r = Invoke-WebRequest -Uri "$script:Root$Page" -WebSession $script:Session -ErrorAction Stop
    $m = $r.Content | Select-String -Pattern 'name="__RequestVerificationToken"[^>]*value="([^"]+)"'
    if (-not $m) { throw "No antiforgery token on $Page — is $script:Root the app?" }
    return @{ Token = $m.Matches.Groups[1].Value; Content = $r.Content }
}

<#
  Whether we are actually signed in, asked rather than assumed: /forge/workflows requires
  authorisation, so a 401 is the answer that the cookie is not good. Both the register and the login
  forms answer 200 with the error rendered into the page, so neither status code proves anything.
#>
function Test-SignedIn { return (Invoke-Api '/forge/workflows').Ok }

<#
  Every run makes its own account. There are no credentials to pass and none to keep: a run is a fresh
  user with an empty history, so nothing it generates is mixed into anyone's library and no result
  depends on what a previous run left behind. Registering signs the new account in, so there is no
  second step.
#>
function Connect-App {
    # One session for the whole run: the antiforgery cookie set here is half of the token pair, and
    # fetching the form on a different session invalidates it.
    $null = Invoke-WebRequest -Uri "$script:Root/account/register" -SessionVariable sess -ErrorAction Stop
    $script:Session = $sess

    $script:Account  = "smoke-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    $throwawayPassword = [guid]::NewGuid().ToString('N')   # never reused, never stored, never printed

    Write-Step "Registering $script:Account"
    $page = Get-AntiForgeryToken '/account/register'
    if ($page.Content -match 'name="code"' -and -not $RegistrationCode) {
        throw "This instance requires a registration code (Auth:RegistrationCode is set). Pass -RegistrationCode."
    }

    $form = @{
        __RequestVerificationToken = $page.Token
        username = $script:Account; password = $throwawayPassword; displayName = $script:Account
        code = $RegistrationCode; returnUrl = '/'
    }
    $null = Invoke-WebRequest -Uri "$script:Root/account/register" -Method Post -Body $form `
                              -WebSession $script:Session -MaximumRedirection 5 -ErrorAction Stop

    if (-not (Test-SignedIn)) {
        throw "Registration did not sign us in. The register form answered without an authorised session."
    }
    Write-Ok "registered and signed in as $script:Account"
}

# --- models -----------------------------------------------------------------------------------

<#
  Every slot is bound BY NAME, one explicit line per slot, below.

  There is no matcher here and no "pick the first file of the right kind". `candidates` is empty for
  all 140 slots on a real install, and `available` is every file of that KIND -- so every Unet slot is
  offered the same 60 files and every "Other" slot the same 27, mixing LoRAs, CLIP-vision and
  IP-Adapters into one list. Choosing from those automatically is how a run ends up rendering
  Chroma1-Base for chroma1-hd and calling it a pass: a wrong model still produces a plausible image,
  and bindings are machine-wide, so it leaves that behind on the box.

  So the choice is authored. Every slot names the file that is CORRECT for it, and `Bind` proves it:
  it re-reads the models list, checks the name is actually on offer for that slot, PUTs it, and reads
  the slot back to confirm the binding took.

  A correct file that is not on offer is an ERROR. There is no exception list, no "this one is not
  downloaded" note, and no slot excused from the run -- a slot excused in advance is a slot this test
  does not test, and those are precisely the ones worth testing. Every one of the 140 has a line, the
  line names the right file, and the run reports which of them the app could not supply.
#>

$script:BindOk = 0; $script:BindFail = @(); $script:BindNodes = 0
$script:BindPlan = @(); $script:NodePlan = @()

<#
  One slot, the way the page does it: read /forge/catalog/status (the models list -- one call serves
  every slot's dropdown, which is exactly the request models.js makes), select $FileName from the
  options that slot offers, PUT it, then read it back. The page reloads its state after every write for
  the same reason: binding a slot changes what the catalogue reports, so the next choice must be made
  against the new answer rather than a stale one.
#>
function Bind {
    param(
        [Parameter(Mandatory, Position = 0)][string] $SlotId,
        [Parameter(Mandatory, Position = 1)][string] $FileName
    )
    $script:BindPlan += [pscustomobject]@{ Slot = $SlotId; File = $FileName }
}

<#
  One read of the models list, then every PUT at once.

  Nothing is read back afterwards. The PUT's own response already says whether it was accepted, so a
  second status pull only re-asks a question that has been answered and doubles the cost of the path
  that works.

  The PUTs run concurrently because nothing orders them: binding a slot does not change which FILES
  exist, so no slot's options depend on another slot having been filled. Serially they were 133 full
  round trips, which is how binding came to be 85% of a run with the GPU idle throughout.
#>
function Invoke-BindPlan {
    $status = Invoke-Api '/forge/catalog/status'
    if (-not $status.Ok) { $script:BindFail += "models list unreadable — $($status.Error)"; return }

    $slots = @{}
    foreach ($sl in @($status.Data.slots)) { $slots[[string]$sl.id] = $sl }

    $todo = @()
    foreach ($p in $script:BindPlan) {
        $slot = $slots[$p.Slot]
        if (-not $slot) { $script:BindFail += "$($p.Slot) : no such slot in the catalogue"; continue }

        # The options that slot's <select> carries — candidates first, then the rest, same as the page.
        $offered = @($slot.candidates) + @($slot.available) | Where-Object { $_ }
        if ($p.File -notin $offered) {
            $script:BindFail += "$($p.Slot) : '$($p.File)' is not offered for this slot ($(@($offered).Count) files of kind $($slot.kind))"
            continue
        }

        # Already correct: the binding is machine-wide and survives, so re-PUTting it is pure cost.
        if ([string]$slot.boundFile -eq $p.File) {
            $script:BindOk++
            Write-Host ("   {0,-40} {1}" -f $p.Slot, $p.File) -ForegroundColor DarkGreen
            continue
        }
        $todo += $p
    }

    if ($todo.Count) {
        $root = $script:Root
        $sess = $script:Session
        # Default throttle: how many run at once is PowerShell's business, not a number invented here.
        $results = $todo | ForEach-Object -Parallel {
            $body = @{ slotId = $_.Slot; fileName = $_.File } | ConvertTo-Json -Compress
            try {
                $null = Invoke-WebRequest -Uri "$using:root/forge/catalog/binding" -Method Put -WebSession $using:sess `
                            -Body $body -ContentType 'application/json' -ErrorAction Stop
                [pscustomobject]@{ Slot = $_.Slot; File = $_.File; Ok = $true; Error = $null }
            }
            catch {
                $text = $null
                try { $text = $_.ErrorDetails.Message } catch { }
                $msg = $text
                if ($text) { try { $j = $text | ConvertFrom-Json; if ($j.error) { $msg = $j.error } } catch { } }
                if (-not $msg) { $msg = $_.Exception.Message }
                [pscustomobject]@{ Slot = $_.Slot; File = $_.File; Ok = $false; Error = $msg }
            }
        }

        foreach ($r in $results) {
            if ($r.Ok) {
                $script:BindOk++
                Write-Host ("   {0,-40} {1}" -f $r.Slot, $r.File) -ForegroundColor DarkGreen
            }
            else { $script:BindFail += "$($r.Slot) : PUT refused — $($r.Error)" }
        }
    }

    # Node slots are judged from the same read: whether ComfyUI has a node registered does not change
    # because a file was bound to some other slot.
    foreach ($n in $script:NodePlan) {
        $wantedBy = @($status.Data.workflows | Where-Object { $n.Slot -in @($_.requiredSlots) })
        if (-not $wantedBy) { $script:BindFail += "$($n.Slot) : no configuration requires this slot"; continue }
        $unsatisfied = @($wantedBy | Where-Object { $n.Slot -in @($_.missingSlots) })
        if ($unsatisfied.Count) {
            $script:BindFail += "$($n.Slot) : ComfyUI has not registered '$($n.Node)' — $($unsatisfied.Count) of $($wantedBy.Count) configuration(s) blocked"
            continue
        }
        $script:BindNodes++
        Write-Host ("   {0,-40} node {1}" -f $n.Slot, $n.Node) -ForegroundColor DarkGreen
    }
}

<#
  A slot ComfyUI satisfies by having a NODE registered rather than by a file, so there is nothing to
  PUT. It is still asserted, not assumed: the status payload does not expose node presence on the slot
  (a node slot's boundFile is always null), but it does expose it through the workflows -- a slot whose
  node is absent is listed in `missingSlots` of every configuration that needs it. Absent is an error
  here exactly as a missing file is.
#>
function RequireNode {
    param(
        [Parameter(Mandatory, Position = 0)][string] $SlotId,
        [Parameter(Mandatory, Position = 1)][string] $NodeClass
    )
    $script:NodePlan += [pscustomobject]@{ Slot = $SlotId; Node = $NodeClass }
}

# --- one workflow -----------------------------------------------------------------------------

<#
  Poll until the job reaches a terminal phase. There is no deadline: the app reports
  queued / running / done / error itself, and a clock invented here would turn a slow render into a
  false failure.
#>
function Wait-Job {
    param([Parameter(Mandatory)][string] $JobId)

    while ($true) {
        Start-Sleep -Seconds $PollSeconds
        $r = Invoke-Api "/forge/result/$JobId"
        if (-not $r.Ok) { return [pscustomobject]@{ Status = 'error'; Error = "poll failed: $($r.Error)"; ImageId = $null } }

        switch ($r.Data.status) {
            'done'      { return [pscustomobject]@{ Status = 'done';      Error = $null;         ImageId = $r.Data.id } }
            'error'     { return [pscustomobject]@{ Status = 'error';     Error = $r.Data.error; ImageId = $null } }
            'cancelled' { return [pscustomobject]@{ Status = 'cancelled'; Error = 'cancelled';   ImageId = $null } }
        }
    }
}

<#
  What to send THIS workflow, from what it says about itself.

  `takesPrompt` is the authority on whether there is text conditioning at all -- SeedVR2 is a restorer
  with no text encoder in the graph. `promptSemantics` separates the two kinds of edit text: WholeImage
  means a full prompt describing the finished picture (inpaint, outpaint), anything else means an
  instruction describing a change (Kontext, Qwen). The guide's `examples` carries a prompt written for
  that specific model, which beats anything written here.

  No aspect is sent. Every configuration defines its own aspect map and resolution envelope, so the
  workflow's own default is the one value guaranteed to exist for it.
#>
function Get-Inputs {
    param([Parameter(Mandatory)] $Workflow, $Guide)

    if ($Workflow.takesPrompt -eq $false) { return @{ Text = ''; Negative = $null } }

    $text = $null
    if ($Guide -and $Guide.examples -and @($Guide.examples).Count -gt 0) { $text = @($Guide.examples)[0] }
    elseif ($Workflow.card -and $Workflow.card.example) { $text = [string]$Workflow.card.example }

    if (-not $text) {
        $wholeImage = ([string]$Workflow.promptSemantics) -eq 'WholeImage'
        $text = if ($Workflow.kind -eq 'edit' -and -not $wholeImage) { 'make the background a sunny park' }
                else { '1girl, solo, standing, plain background' }
    }

    $negative = $null
    if ($Guide -and $Guide.negativeSupported -eq $true) { $negative = '' }
    return @{ Text = $text; Negative = $negative }
}

<#
  The mask an inpaint needs. The browser paints one on a canvas and uploads the PNG; this draws the
  same thing -- white is the region to regenerate -- sized to the source, because a mask of different
  dimensions is not a mask of that image.

  A centred rectangle covering the middle quarter: big enough that the model has something to do,
  small enough that the original is what most of the result is judged against.
#>
function New-MaskUpload {
    param([Parameter(Mandatory)][int] $Width, [Parameter(Mandatory)][int] $Height)

    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.Clear([System.Drawing.Color]::Black)
            $w = [int]($Width / 2); $h = [int]($Height / 2)
            $g.FillRectangle([System.Drawing.Brushes]::White, [int](($Width - $w) / 2), [int](($Height - $h) / 2), $w, $h)
        } finally { $g.Dispose() }

        $file = Join-Path ([IO.Path]::GetTempPath()) ("smoke-mask-" + [guid]::NewGuid().ToString('N') + ".png")
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        try {
            $r = Invoke-WebRequest -Uri "$script:Root/forge/upload" -Method Post `
                                   -Form @{ image = Get-Item -LiteralPath $file } `
                                   -WebSession $script:Session -ErrorAction Stop
            return ($r.Content | ConvertFrom-Json).id
        } finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
    } finally { $bmp.Dispose() }
}

<# The source image's real dimensions, so a mask matches it and a pad can be bounded by the envelope. #>
function Get-ImageSize {
    param([Parameter(Mandatory)][string] $ImageId)
    $r = Invoke-Api "/forge/image/$ImageId/info"
    if ($r.Ok -and $r.Data.width) { return @{ Width = [int]$r.Data.width; Height = [int]$r.Data.height } }
    return $null
}

<#
  How far an outpaint should extend. pad_left/top/right/bottom are deliberately NOT exposed params --
  the frame editor is the only thing that supplies them in the UI -- so they go as overrides, and left
  at their default of 0 the workflow pads by nothing and hands the source straight back.

  Derived rather than picked: an eighth of each dimension, snapped down to the configuration's declared
  resolution step, and clamped so the padded canvas still fits the envelope the workflow says it
  supports. A step of at least one increment, so it always actually outpaints something.
#>
function Get-OutpaintPads {
    param([Parameter(Mandatory)] $Size, $Envelope)

    $step = if ($Envelope -and $Envelope.step -gt 0) { [int]$Envelope.step } else { 8 }
    $maxW = if ($Envelope -and $Envelope.max_w -gt 0) { [int]$Envelope.max_w } else { [int]::MaxValue }
    $maxH = if ($Envelope -and $Envelope.max_h -gt 0) { [int]$Envelope.max_h } else { [int]::MaxValue }

    function Fit([int]$source, [int]$max) {
        $want = [math]::Floor($source / 8 / $step) * $step          # an eighth, on the grid
        $room = [math]::Floor(($max - $source) / 2 / $step) * $step # what the envelope leaves per side
        $pad  = [math]::Min($want, [math]::Max($room, 0))
        if ($pad -lt $step -and $room -ge $step) { $pad = $step }
        return [int]$pad
    }

    $x = Fit $Size.Width  $maxW
    $y = Fit $Size.Height $maxH
    return @{ pad_left = $x; pad_right = $x; pad_top = $y; pad_bottom = $y }
}

<#
  Run ONE configuration: the block below supplies its id, whether it is an edit, what extra input it
  needs, and the overrides that make it fast. Everything else -- the text it wants, whether it reads a
  negative -- still comes from the workflow's own metadata, because that is a fact about the model
  rather than a choice for a caller.

  It records and returns; it never throws. A configuration that is not runnable on this box, is
  refused, or errors is one line in the report and the next block runs.
#>
function Test-Workflow {
    param(
        [Parameter(Mandatory, Position = 0)][string] $Id,
        [switch]   $Edit,
        [ValidateSet('none', 'mask', 'pad')][string] $Needs = 'none',
        [hashtable] $Overrides
    )

    if ($script:Only -and $script:Only -notcontains $Id) { return }

    $Workflow = $script:Runnable[$Id]
    if (-not $Workflow) {
        $miss = $script:NotReady[$Id]
        $why  = if ($miss) { "not runnable: $($miss -join ', ') not set" }
                else       { 'not in this build''s catalogue' }
        $script:Results += [pscustomobject]@{ Id = $Id; Kind = $(if ($Edit) { 'edit' } else { 'generate' })
                                              Status = 'unavailable'; Seconds = 0; Error = $why; ImageId = $null; Sent = $null }
        Write-Host ("   {0,-34} unavailable — {1}" -f $Id, $why) -ForegroundColor DarkGray
        return
    }

    $r = $null
    try   { $r = Invoke-One -Workflow $Workflow -Needs $Needs -Overrides $Overrides }
    catch {
        # A bug in this harness must not end the pass either.
        $r = [pscustomobject]@{ Id = $Id; Kind = 'unknown'; Status = 'harness-error'; Seconds = 0
                                Error = $_.Exception.Message; ImageId = $null; Sent = $null }
    }
    $script:Results += $r

    <#
      Every edit is fed the SMALLEST image this run made, not the latest one.

      An edit's cost is set by its source, not by any knob on the edit itself -- 13 of these
      configurations expose no size at all and are priced entirely by what they are handed. Taking
      whatever generated last means an edit following hunyuanimage21 works on 1536x1536 for no reason,
      and the run order decides the bill. The smallest successful generation is 64x64, it exercises the
      identical path, and it is the same picture as far as "did this workflow run" is concerned.
    #>
    <#
      And the same for CLIPS. Three configurations declare sourceMedia=video -- they matte, deflicker or
      quantise a moving picture, not a still -- and every one of them used to skip for want of a source,
      so three of the 147 were never exercised at all. The run produces clips itself (any workflow whose
      `media` is video), and the endpoint takes the same imageId for both: the server sees
      SourceMedia=Video and transcodes that media to mp4 before handing it to LoadVideo. So a clip this
      run made is a usable source, and the cheapest one is picked for the same reason as the image.
    #>
    if ($r.ImageId) {
        $w = if ($Overrides -and $Overrides.ContainsKey('width'))  { [int]$Overrides.width }  else { 0 }
        $h = if ($Overrides -and $Overrides.ContainsKey('height')) { [int]$Overrides.height } else { 0 }
        $len = if ($Overrides -and $Overrides.ContainsKey('length')) { [int]$Overrides.length } else { 1 }
        $area = if ($w -and $h) { $w * $h } else { [int]::MaxValue }

        if ([string]$Workflow.media -eq 'video') {
            $cost = if ($area -eq [int]::MaxValue) { [long]::MaxValue } else { [long]$area * [long]$len }
            if ($cost -lt $script:SourceVideoCost) {
                $script:SourceVideoCost = $cost
                $script:SourceVideo = $r.ImageId
            }
        }
        elseif ($area -lt $script:SourceArea) {
            $script:SourceArea = $area
            $script:SourceImage = $r.ImageId
        }
    }

    switch ($r.Status) {
        'done'    { Write-Host ("   {0,-34} done in {1}s" -f $r.Id, $r.Seconds) -ForegroundColor Green }
        'skipped' { Write-Host ("   {0,-34} skipped — {1}" -f $r.Id, $r.Error) -ForegroundColor DarkGray }
        default   { Write-Host ("   {0,-34} {1} — {2}" -f $r.Id, $r.Status, $r.Error) -ForegroundColor Red }
    }
}

<# The submit-and-wait behind every block. #>
function Invoke-One {
    param([Parameter(Mandatory)] $Workflow, [string] $Needs, [hashtable] $Overrides)

    # Start every render from a flushed renderer, not whatever the previous configuration left resident.
    Clear-Vram

    $id   = $Workflow.id
    $kind = if ($Workflow.kind) { $Workflow.kind } else { 'generate' }
    $t0   = Get-Date
    $in   = Get-Inputs -Workflow $Workflow -Guide $script:Guides[[string]$id]
    $SourceImageId = $script:SourceImage

    if ($kind -eq 'edit') {
        # Which SOURCE this needs is decided before either guard: a clip-consumer is not missing an
        # image, it is missing a clip, and asking the image question first reports the wrong shortage.
        # The field is the same for both -- the server transcodes the media once it sees SourceMedia=Video.
        if ([string]$Workflow.sourceMedia -eq 'video') {
            if (-not $script:SourceVideo) {
                return [pscustomobject]@{ Id = $id; Kind = $kind; Status = 'skipped'; Seconds = 0
                                          Error = 'consumes a clip, and no video generation has succeeded yet'; ImageId = $null; Sent = $in.Text }
            }
            $SourceImageId = $script:SourceVideo
        }
        elseif (-not $SourceImageId) {
            return [pscustomobject]@{ Id = $id; Kind = $kind; Status = 'skipped'; Seconds = 0
                                      Error = 'no source image — nothing this run generated succeeded yet'; ImageId = $null; Sent = $in.Text }
        }

        $body = @{ workflow = $id; instruction = $in.Text; imageId = $SourceImageId }
        if ($null -ne $in.Negative) { $body.negativePrompt = $in.Negative }

        # Which extra input this one needs is declared by its block, not sniffed from its name.
        if ($Needs -eq 'mask') {
            $size = Get-ImageSize -ImageId $SourceImageId
            if (-not $size) {
                return [pscustomobject]@{ Id = $id; Kind = $kind; Status = 'skipped'; Seconds = 0
                                          Error = "could not read the source image's size, so no mask could be made"; ImageId = $null; Sent = $in.Text }
            }
            $body.maskImageId = New-MaskUpload -Width $size.Width -Height $size.Height
        }
        elseif ($Needs -eq 'pad') {
            $size = Get-ImageSize -ImageId $SourceImageId
            if (-not $size) {
                return [pscustomobject]@{ Id = $id; Kind = $kind; Status = 'skipped'; Seconds = 0
                                          Error = "could not read the source image's size, so no pad could be sized"; ImageId = $null; Sent = $in.Text }
            }
            $settings = Invoke-Api "/forge/catalog/config/$id/settings"
            $envelope = if ($settings.Ok) { $settings.Data.resolution } else { $null }
            $body.overrides = Get-OutpaintPads -Size $size -Envelope $envelope
        }
        if ($Overrides) {
            if (-not $body.overrides) { $body.overrides = @{} }
            foreach ($k in $Overrides.Keys) { $body.overrides[$k] = $Overrides[$k] }
        }
        $post = Invoke-Api '/forge/edit' -Method Post -Body $body
    }
    else {
        $body = @{ workflow = $id; prompt = $in.Text }
        if ($null -ne $in.Negative) { $body.negativePrompt = $in.Negative }
        if ($Overrides) { $body.overrides = $Overrides }
        $post = Invoke-Api '/forge/generate' -Method Post -Body $body
    }

    if (-not $post.Ok) {
        return [pscustomobject]@{ Id = $id; Kind = $kind; Status = 'rejected'
                                  Seconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
                                  Error = $post.Error; ImageId = $null; Sent = $in.Text }
    }

    $jobId = $post.Data.jobId
    if (-not $jobId) {
        return [pscustomobject]@{ Id = $id; Kind = $kind; Status = 'rejected'; Seconds = 0
                                  Error = 'accepted but returned no jobId'; ImageId = $null; Sent = $in.Text }
    }

    $done = Wait-Job -JobId $jobId
    return [pscustomobject]@{ Id = $id; Kind = $kind; Status = $done.Status
                              Seconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
                              Error = $done.Error; ImageId = $done.ImageId; Sent = $in.Text }
}

# --- the run ----------------------------------------------------------------------------------

Connect-App

if ($SkipBinding) { Write-Step "Binding skipped" } else {

Write-Step "Binding model slots"

# Names are the file each slot is FOR. Where the model is on this disk the name is that file; where it
# is not, the name is the model's published filename and the run reports that the app could not offer it.

# --- diffusion models -------------------------------------------------------------------------
Bind 'anima-base-v1-0'                      'anima-base-v1.0_int8_convrot.safetensors'
Bind 'boogu-image-base'                     'boogu_image_base_int8_convrot.safetensors'
Bind 'boogu-image-edit'                     'boogu_image_edit_int8_convrot.safetensors'
Bind 'chroma1-base'                         'Chroma1-Base.safetensors'
Bind 'chroma1-flash'                        'Chroma1-HD-Flash.safetensors'
Bind 'chroma1-hd'                           'Chroma1-HD.safetensors'
Bind 'chroma1-radiance-x0'                  'chroma-radiance-x0.safetensors'
Bind 'chronoedit-14b'                       'ChronoEdit-14B_int8_convrot.safetensors'
Bind 'firered-image-edit-1-1'               'FireRed-Image-Edit-1.1-Q8_0.gguf'
Bind 'flux1-dev'                            'flux1-dev-Q8_0.gguf'
Bind 'flux1-schnell'                        'flux1-schnell-Q8_0.gguf'
Bind 'flux1-dev-kontext'                    'flux1-dev-kontext_fp8_scaled.safetensors'
Bind 'flux1-kontext-dev-diffusers'          'FLUX.1-Kontext-dev'
Bind 'flux1-krea-dev'                       'flux1-krea-dev_fp8_scaled.safetensors'
Bind 'flux1-fill-dev'                       'flux1-fill-dev.safetensors'
Bind 'flux2-dev'                            'flux2-dev-Q4_K_M.gguf'
Bind 'flux-2-klein-4b'                      'flux-2-klein-4b.safetensors'
Bind 'flux-2-klein-base-4b'                 'flux-2-klein-base-4b.safetensors'
Bind 'flux-2-klein-9b'                      'flux-2-klein-9b_int8_convrot.safetensors'
Bind 'hidream-i1-dev'                       'hidream-i1-dev-Q8_0.gguf'
Bind 'hidream-i1-fast'                      'hidream-i1-fast-Q8_0.gguf'
Bind 'hidream-i1-full'                      'hidream-i1-full-Q8_0.gguf'
Bind 'hunyuanimage21'                       'hunyuanimage2.1_int8_convrot.safetensors'
Bind 'hunyuanimage21-distilled'             'hunyuanimage2.1_distilled_int8_convrot.safetensors'
Bind 'ideogram4'                            'ideogram4_int8_convrot.safetensors'
Bind 'ideogram4-unconditional'              'ideogram4_unconditional_int8_convrot.safetensors'
Bind 'krea2-raw'                            'krea2_raw_int8_convrot.safetensors'
Bind 'krea2-turbo'                          'krea2_turbo_fp8_scaled.safetensors'
Bind 'longcat-image-edit'                   'LongCat-Image-Edit-Q5_K_M.gguf'
Bind 'longcat-image-edit-turbo'             'LongCat-Image-Edit-Turbo-Q5_K_M.gguf'
Bind 'ltx-2-19b-dev'                        'ltx-2-19b-dev-Q6_K.gguf'
Bind 'ltx-2-19b-distilled'                  'ltx-2-19b-distilled-Q6_K.gguf'
Bind 'ltx-2-3-22b-distilled-1-1'            'LTX-2.3-22B-distilled-1.1-Q4_K_M.gguf'
Bind 'mage-flow'                            'mage_flow_int8_convrot.safetensors'
Bind 'mage-flow-edit'                       'mage_flow_edit_int8_convrot.safetensors'
Bind 'mage-flow-edit-turbo'                 'mage_flow_edit_turbo_int8_convrot.safetensors'
Bind 'mage-flow-turbo'                      'mage_flow_turbo_int8_convrot.safetensors'
Bind 'minimax-h3-fl2va'                     'minimax_h3_fl2va_pruned_int8_convrot.safetensors'
Bind 'minimax-h3-ref2va'                    'minimax_h3_ref2va_pruned_int8_convrot.safetensors'
Bind 'step1x-edit-i1258'                    'step1x-edit-i1258-FP8.safetensors'
Bind 'photanima-v21-noturbo'                'photanima_v21_noTurbo.safetensors'
Bind 'pixeldit-1300m-1024px'                'pixeldit_1300m_1024px_bf16.safetensors'
Bind 'qwen-image'                           'qwen-image-Q6_K.gguf'
Bind 'qwen-image-2512'                      'qwen-image-2512-int8-ConvRot.safetensors'
Bind 'qwen-image-edit-2511'                 'qwen_image_edit_2511_int8_convrot.safetensors'
Bind 'z-image'                              'z_image_bf16.safetensors'
Bind 'z-image-turbo'                        'z_image_turbo_bf16.safetensors'

# --- checkpoints ------------------------------------------------------------------------------
Bind 'autismmixsdxl-autismmixconfetti'      'autismmixSDXL_autismmixConfetti.safetensors'
Bind 'counterfeit-v3-0'                     'Counterfeit-V3.0_fp16.safetensors'
Bind 'ltxv-13b-0-9-8-distilled'             'ltxv-13b-0.9.8-distilled-fp8.safetensors'
Bind 'ltxv-2b-0-9-8-distilled'              'ltxv-2b-0.9.8-distilled-fp8.safetensors'
Bind 'lumina-2'                             'lumina_2.safetensors'
Bind 'ponydiffusionv6xl-v6startwiththisone' 'ponyDiffusionV6XL_v6StartWithThisOne.safetensors'
Bind 'qwen-rapid-aio-nsfw-v5-3'             'Qwen-Rapid-AIO-NSFW-v5.3.safetensors'
Bind 'sd-xl-base-1-0'                       'sd_xl_base_1.0.safetensors'
Bind 'v1-5-pruned-emaonly'                  'v1-5-pruned-emaonly.safetensors'
Bind 'v2-1-768-ema-pruned'                  'v2-1_768-ema-pruned-fp16.safetensors'
Bind 'sd3-5-large'                          'sd3.5_large.safetensors'
Bind 'sd3-5-large-turbo'                    'sd3.5_large_turbo.safetensors'
Bind 'sd3-5-medium'                         'sd3.5_medium_incl_clips_t5xxlfp8scaled.safetensors'

# --- video --------------------------------------------------------------------------------------
Bind 'hunyuan-video-t2v-720p'               'hunyuan-video-t2v-720p-Q8_0.gguf'
Bind 'hunyuanvideo15-480p-t2v'                'hunyuanvideo1.5_480p_t2v_fp16.safetensors'
Bind 'hunyuanvideo15-480p-t2v-cfg-distilled'  'hunyuanvideo1.5_480p_t2v_cfg_distilled_fp16.safetensors'
Bind 'hunyuanvideo15-480p-i2v'                'hunyuanvideo1.5_480p_i2v_fp16.safetensors'
Bind 'hunyuanvideo15-480p-i2v-cfg-distilled'  'hunyuanvideo1.5_480p_i2v_cfg_distilled_fp16.safetensors'
Bind 'hunyuanvideo15-480p-i2v-step-distilled' 'hunyuanvideo1.5_480p_i2v_step_distilled_fp16.safetensors'
Bind 'hunyuanvideo15-720p-t2v'                'hunyuanvideo1.5_720p_t2v_fp16.safetensors'
Bind 'hunyuanvideo15-720p-i2v'                'hunyuanvideo1.5_720p_i2v_fp16.safetensors'
Bind 'hunyuanvideo15-720p-i2v-cfg-distilled'  'hunyuanvideo1.5_720p_i2v_cfg_distilled_fp16.safetensors'
Bind 'hunyuanvideo15-1080p-sr-distilled'      'hunyuanvideo1.5_1080p_sr_distilled_fp16.safetensors'
Bind 'wan2-2-t2v-high-noise-14b'            'wan2.2_t2v_high_int8_ConvRot.safetensors'
Bind 'wan2-2-t2v-low-noise-14b'             'wan2.2_t2v_low_int8_ConvRot.safetensors'
Bind 'wan2-2-i2v-high-noise-14b'            'wan2.2_i2v_high_int8_convrot.safetensors'
Bind 'wan2-2-i2v-low-noise-14b'             'wan2.2_i2v_low_int8_convrot.safetensors'
Bind 'wan2-2-ti2v-5b'                       'wan2.2_ti2v_5B_fp16.safetensors'

# --- text encoders ----------------------------------------------------------------------------
Bind 'byt5-small-glyphxl'                   'byt5_small_glyphxl_fp16.safetensors'
Bind 'clip-l'                               'clip_l.safetensors'
Bind 'clip-g'                               'clip_g.safetensors'
Bind 'clip-l-hidream'                       'clip_l_hidream.safetensors'
Bind 'clip-g-hidream'                       'clip_g_hidream.safetensors'
Bind 'gemma-2-2b-it-elm'                    'gemma_2_2b_it_elm_bf16.safetensors'
Bind 'gemma-3-12b-it'                       'gemma_3_12B_it_fp4_mixed.safetensors'
Bind 'llama-3-1-8b-instruct'                'llama_3.1_8b_instruct_fp8_scaled.safetensors'
Bind 'llava-llama3'                         'llava_llama3_fp8_scaled.safetensors'
Bind 'ltx-2-19b-dev-embeddings-connectors'  'ltx-2-19b-dev_embeddings_connectors.safetensors'
Bind 'ltx-2-3-text-projection'              'ltx-2.3_text_projection_bf16.safetensors'
Bind 'mistral-3-small-flux2'                'mistral_3_small_flux2_fp8.safetensors'
Bind 'qwen-2-5-vl-7b'                       'qwen_2.5_vl_7b_fp8_scaled.safetensors'
Bind 'qwen-3-06b-base'                      'qwen_3_06b_base.safetensors'
Bind 'qwen-3-4b'                            'qwen_3_4b.safetensors'
Bind 'qwen-3-8b'                            'qwen_3_8b_fp8mixed.safetensors'
Bind 'qwen3vl-4b'                           'qwen3vl_4b_fp8_scaled.safetensors'
Bind 'qwen3vl-8b-boogu'                     'qwen3vl_8b_fp8_scaled.safetensors'
Bind 't5xxl'                                't5xxl_fp8_e4m3fn_scaled.safetensors'
Bind 'qwen3vl-32b-minimax-h3'               'qwen3vl_32b_minimax_h3_int8_convrot.safetensors'
Bind 'umt5-xxl'                             'umt5_xxl_fp8_e4m3fn_scaled.safetensors'

# --- VAEs -------------------------------------------------------------------------------------
Bind 'ae'                                   'ae.safetensors'
Bind 'flux1-vae'                            'flux1_vae_bf16.safetensors'
Bind 'flux2-vae'                            'flux2-vae.safetensors'
Bind 'hunyuan-image-2-1-vae'                'hunyuan_image_2.1_vae_fp16.safetensors'
Bind 'hunyuan-video-vae'                    'hunyuan_video_vae_bf16.safetensors'
Bind 'hunyuanvideo15-vae'                   'hunyuanvideo15_vae_fp16.safetensors'
Bind 'ltx-2-19b-dev-video-vae'              'ltx-2-19b-dev_video_vae.safetensors'
Bind 'ltx-2-3-video-vae'                    'LTX23_video_vae_bf16.safetensors'
Bind 'pixel-space-vae'                      'pixel_space_vae.safetensors'
Bind 'qwen-image-vae'                       'qwen_image_vae.safetensors'
Bind 'seedvr2-vae'                          'ema_vae_fp16.safetensors'
Bind 'wan-2-1-vae'                          'wan_2.1_vae.safetensors'
Bind 'wan2-2-vae'                           'wan2.2_vae.safetensors'
Bind 'mage-flow-vae'                        'mage_flow_vae_bf16.safetensors'
Bind 'minimax-h3-audio-vae'                 'minimax_h3_audio_vae_fp32.safetensors'
Bind 'minimax-h3-video-vae'                 'minimax_h3_video_vae_fp16.safetensors'
Bind 'step1x-edit-vae'                      'step1x-edit-vae.safetensors'

# --- LoRAs ------------------------------------------------------------------------------------
Bind 'animatelcm-sd15-t2v-lora'             'AnimateLCM_sd15_t2v_lora.safetensors'
Bind 'chronoedit-distill-lora'              'chronoedit_distill_lora.safetensors'
Bind 'nipplediffusion-zimage-lora'          'nipplediffusion_zimage_v1.safetensors'
Bind 'wan-anime-test-lora'                  'wan22_5b_lora_alpha2_000004800.safetensors'
Bind 'minimax-h3-turbo-lora'                'minimax_h3_fl2v_lightx2v_turbo_4step_v0.1_comfy.safetensors'
Bind 'wan-flat-color-lora'                  'wan_flat_color_2.2.5b_v2.safetensors'

# --- controlnets, motion, adapters, vision ------------------------------------------------------
Bind 'anima-lllite-inpainting-v2'           'anima-lllite-inpainting-v2.safetensors'
Bind 'controlnet-sd15-lineart'              'control_v11p_sd15_lineart.pth'
Bind 'v3-sd15-sparsectrl-rgb'               'v3_sd15_sparsectrl_rgb.ckpt'
Bind 'qwen-image-instantx-controlnet-inpainting' 'Qwen-Image-InstantX-ControlNet-Inpainting_int8_convrot.safetensors'
Bind 'animatediff-lightning-8step-comfyui'  'animatediff_lightning_8step_comfyui.safetensors'
Bind 'animatelcm-sd15-t2v'                  'AnimateLCM_sd15_t2v.ckpt'
Bind 'mm-sdxl-v10-beta'                     'mm_sdxl_v10_beta.ckpt'
Bind 'v3-sd15-mm'                           'v3_sd15_mm.ckpt'
Bind 'ip-adapter-plus-sd15'                 'ip-adapter-plus_sd15.safetensors'
Bind 'clip-vit-h-14-laion2b-s32b-b79k'      'CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors'
Bind 'sigclip-vision-patch14-384'           'sigclip_vision_patch14_384.safetensors'

# --- upscalers and restorers ---------------------------------------------------------------------
Bind 'anime-sharp-v2-rplksr-sharp-2x'       '2x-AnimeSharpV2_RPLKSR_Sharp.pth'
Bind 'nomos2-hq-dat2-4x'                    '4xNomos2_hq_dat2.safetensors'
Bind 'seedvr2-3b'                           'seedvr2_ema_3b-Q8_0.gguf'
Bind 'hunyuanvideo15-latent-upsampler-1080p' 'hunyuanvideo15_latent_upsampler_1080p.safetensors'

# --- satisfied by a registered node, not a file ----------------------------------------------------
RequireNode 'comfyui-advanced-controlnet'   'ACN_AdvancedControlNetApply'
RequireNode 'comfyui-anima-lllite'          'AnimaLLLiteApply'
RequireNode 'comfyui-animatediff-evolved'   'ADE_AnimateDiffLoaderGen1'
RequireNode 'comfyui-conditioning-rebalance' 'RebalanceGuider'
RequireNode 'comfyui-ipadapter-plus'        'IPAdapterAdvanced'
RequireNode 'comfyui-seedvr2-node'          'SeedVR2LoadDiTModel'

Invoke-BindPlan

Write-Ok "$script:BindOk bound, $script:BindNodes node slot(s) satisfied"
if ($script:BindFail.Count) {
    Write-Bad "$($script:BindFail.Count) the app could not supply:"
    foreach ($f in $script:BindFail) { Write-Bad "     $f" }
}

# Every slot the catalogue carries must have had a line above. A slot with no line is one this run
# never tried, and a run that quietly covers a subset is the failure mode this whole section exists to
# avoid -- so the count is checked against the catalogue rather than trusted.
$attempted = $script:BindOk + $script:BindNodes + $script:BindFail.Count
$all = Invoke-Api '/forge/catalog/status'
if ($all.Ok -and $attempted -ne @($all.Data.slots).Count) {
    Write-Bad "$attempted slots attempted but the catalogue carries $(@($all.Data.slots).Count) — some slot has no line"
}

$binding = [pscustomobject]@{
    Bound = $script:BindOk; Nodes = $script:BindNodes; Failed = $script:BindFail; Attempted = $attempted
}
}

Write-Step "Reading the workflow library"
$status = Invoke-Api '/forge/catalog/status'
if (-not $status.Ok) { throw "Could not read the catalogue: $($status.Error)" }
$eligible = Invoke-Api '/forge/workflows'
if (-not $eligible.Ok) { throw "Could not read the workflow list: $($eligible.Error)" }

# Indexed by id so a block can look itself up, and so a block naming something this build cannot run
# says exactly which slot is missing rather than vanishing from the report.
$script:Runnable = @{}
foreach ($w in @($eligible.Data)) { $script:Runnable[[string]$w.id] = $w }
$script:NotReady = @{}
# Nothing is hidden on a fresh deploy: the catalogue ships no visibility flag, and per-user hiding is a choice
# this throwaway account never makes. So a not-ready configuration is always one waiting on a model file, and
# `ready` (missing.Count == 0) and "has missing slots" now say the same thing.
foreach ($w in @($status.Data.workflows)) {
    if (-not $w.ready) { $script:NotReady[[string]$w.id] = @($w.missingSlots) }
}

$script:Guides = @{}
$g = Invoke-Api '/forge/prompting'
if ($g.Ok) { foreach ($x in @($g.Data)) { if ($x.name) { $script:Guides[[string]$x.name] = $x } } }

$script:Results   = @()
# The cheapest source any generation produced, and its area. Edits are priced by what they are
# handed, so the run keeps the smallest rather than the most recent.
$script:SourceImage = $null
$script:SourceArea  = [int]::MaxValue
# The cheapest clip the run produced, for the three configurations that consume one.
$script:SourceVideo     = $null
$script:SourceVideoCost = [long]::MaxValue
<#
  `pwsh -File script.ps1 -Only a,b,c` hands the parameter ONE string containing commas, where
  `.\script.ps1 -Only a,b,c` from a prompt hands it three. Splitting here makes both spellings mean the
  same thing -- without it, a -File invocation silently matches no configuration and the run tests
  nothing while exiting 0, which is how this was found.
#>
$script:Only = if ($Only) { @($Only -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { $null }

Write-Ok "$($script:Runnable.Count) runnable, $($script:NotReady.Count) waiting on model files"

<#
  An edit is fed an image this run generated, so a selection containing only edits has nothing to work
  from and every one of them skips. That is a silent nothing: the run ends "successfully" having
  exercised no graph. Say it up front, because the fix is simply to name a generate configuration too.
#>
if ($script:Only) {
    $editOnly = @($script:Only | Where-Object { $script:Runnable[$_] -and [string]$script:Runnable[$_].kind -eq 'edit' })
    $anyGen   = @($script:Only | Where-Object { $script:Runnable[$_] -and [string]$script:Runnable[$_].kind -ne 'edit' })
    if ($editOnly.Count -and -not $anyGen.Count) {
        Write-Bad "-Only names $($editOnly.Count) edit configuration(s) and no generate one; each will skip for want of a source image."
        Write-Bad "Add a cheap generate configuration to -Only (flux2-klein-4b runs at 64x64) so the edits have something to work from."
    }
}

Write-Step "Running the configurations"

# --- generate ---------------------------------------------------------------------------------
# One block per configuration, each carrying the numbers that make IT fast. Every value is bounded
# by what that configuration declares it supports: steps come down to 8 or its own floor, a clip is
# cut to 9 frames (4n+1, the shortest the latent video models accept), and a size is the smallest
# the workflow says it renders. None of this is a default -- a default run of 147 configurations at
# 28-40 steps and full length is hours of GPU for the same pass/fail.

# Anima — 8 steps not 40; 512x512, its smallest supported
Test-Workflow 'anima'                             -Overrides @{ steps = 1; width = 512; height = 512 }

# AutismMix Confetti — 8 steps not 28; 512x512, its smallest supported
Test-Workflow 'autismmix'                         -Overrides @{ steps = 1; width = 512; height = 512 }

# BiRefNet Matte (video)

# Boogu-Image Base — 8 steps not 40; 1024x1024, its smallest supported
Test-Workflow 'boogu-base'                        -Overrides @{ steps = 1; width = 1024; height = 1024 }

# Chroma1-Base — 8 steps not 26; 256x256, its smallest supported
Test-Workflow 'chroma1-base'                      -Overrides @{ steps = 1; width = 256; height = 256 }

# Chroma1-Flash — 256x256, its smallest supported
Test-Workflow 'chroma1-flash'                     -Overrides @{ steps = 1; width = 256; height = 256 }

# Chroma1-HD — 8 steps not 26; 256x256, its smallest supported
Test-Workflow 'chroma1-hd'                        -Overrides @{ steps = 1; width = 256; height = 256 }

# Chroma1-Radiance (pixel-space) — 8 steps not 26; 512x512, its smallest supported
Test-Workflow 'chroma1-radiance'                  -Overrides @{ steps = 1; width = 512; height = 512 }

# Deflicker Auto (video)

# FLUX.1-dev — 8 steps not 28; 256x256, its smallest supported
Test-Workflow 'flux1-dev'                         -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.1-Krea — 8 steps not 28; 256x256, its smallest supported
Test-Workflow 'flux1-krea'                        -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.1-schnell — 256x256, its smallest supported
Test-Workflow 'flux1-schnell'                     -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.2-dev — 8 steps not 20; 64x64, its smallest supported
Test-Workflow 'flux2-dev'                         -Overrides @{ steps = 1; width = 64; height = 64 }

# FLUX.2-Klein 4B Base — 8 steps not 20; 64x64, its smallest supported
Test-Workflow 'flux2-klein-4b-base'               -Overrides @{ steps = 1; width = 64; height = 64 }

# FLUX.2-Klein 4B — 64x64, its smallest supported
Test-Workflow 'flux2-klein-4b'                    -Overrides @{ steps = 1; width = 64; height = 64 }

# FLUX.2-Klein 9B — 64x64, its smallest supported
Test-Workflow 'flux2-klein-9b'                    -Overrides @{ steps = 1; width = 64; height = 64 }

# HiDream-I1 Dev — 8 steps not 28; 768x768, its smallest supported
Test-Workflow 'hidream-dev'                       -Overrides @{ steps = 1; width = 768; height = 768 }

# HiDream-I1 Fast — 8 steps not 16; 768x768, its smallest supported
Test-Workflow 'hidream-fast'                      -Overrides @{ steps = 1; width = 768; height = 768 }

# HiDream-I1 Full — 8 steps not 50; 768x768, its smallest supported
Test-Workflow 'hidream-full'                      -Overrides @{ steps = 1; width = 768; height = 768 }

# HunyuanImage 2.1 Full — 8 steps not 50; 1536x1536, its smallest supported
Test-Workflow 'hunyuanimage21-full'               -Overrides @{ steps = 1; width = 1536; height = 1536 }

# HunyuanImage 2.1 — 1536x1536, its smallest supported
Test-Workflow 'hunyuanimage21'                    -Overrides @{ steps = 1; width = 1536; height = 1536 }

# HunyuanVideo — 8 steps not 20; 9 frames not 73; 544x544, its smallest supported
Test-Workflow 'hunyuanvideo-t2v'                  -Overrides @{ steps = 1; length = 5; width = 544; height = 544 }

# HunyuanVideo 1.5 480p T2V Full — 8 steps not 30; 9 frames not 49; 320x320, its smallest supported
Test-Workflow 'hunyuanvideo15-480p-t2v-full'      -Overrides @{ steps = 1; length = 5; width = 320; height = 320 }

# HunyuanVideo 1.5 (480p T2V) — 8 steps not 20; 9 frames not 49; 320x320, its smallest supported
# The one video generator asked for a REAL clip: length 1 renders a single frame, which saves as a
# still WEBP and cannot be read back by the three clip-consuming editors. HunyuanVideo latents run
# 4k+1, so 5 is the shortest genuine animation, and this is the cheapest generator to ask for one.
Test-Workflow 'hunyuanvideo15-480p-t2v'           -Overrides @{ steps = 1; length = 5; width = 320; height = 320 }

# HunyuanVideo 1.5 (1080p SR T2V) — 8 steps not 20; 9 frames not 49; 320x320, its smallest supported
Test-Workflow 'hunyuanvideo15-t2v-sr'             -Overrides @{ steps = 1; length = 5; width = 320; height = 320 }

# HunyuanVideo 1.5 — 8 steps not 20; 9 frames not 121; 480x480, its smallest supported
Test-Workflow 'hunyuanvideo15-t2v'                -Overrides @{ steps = 1; length = 5; width = 480; height = 480 }

# Ideogram 4 — 8 steps not 20; 256x256, its smallest supported
Test-Workflow 'ideogram4'                         -Overrides @{ steps = 1; width = 256; height = 256 }

# Krea 2 (Base + Turbo Polish) — 8 steps not 28; 1024x1024, its smallest supported
Test-Workflow 'krea2-refine'                      -Overrides @{ steps = 1; width = 1024; height = 1024 }

# Krea 2 Turbo — 1024x1024, its smallest supported
Test-Workflow 'krea2-turbo'                       -Overrides @{ steps = 1; width = 1024; height = 1024 }

# Krea 2 — 8 steps not 28; 1024x1024, its smallest supported
Test-Workflow 'krea2'                             -Overrides @{ steps = 1; width = 1024; height = 1024 }

# LTX-2 dev — 8 steps not 30; 9 frames not 97
Test-Workflow 'ltx2-i2v-dev-pixel'                -Overrides @{ steps = 1; length = 9; width = 128; height = 128 }

# LTX-2 — 9 frames not 97; 32x32, its smallest supported
Test-Workflow 'ltx2-i2v-pixel'                    -Overrides @{ steps = 1; length = 9; width = 128; height = 128 }

# LTX-2.3 22B — 9 frames not 97
Test-Workflow 'ltx23-i2v-pixel'                   -Overrides @{ steps = 1; length = 9; width = 128; height = 128 }

# LTX Video 13B — 9 frames not 97
Test-Workflow 'ltxv-13b-i2v-pixel'                -Overrides @{ steps = 1; length = 9; width = 128; height = 128 }

# LTX Video — 9 frames not 97; 32x32, its smallest supported
Test-Workflow 'ltxv-i2v-pixel'                    -Overrides @{ steps = 1; length = 9; width = 128; height = 128 }

# Lumina-Image 2.0 — 8 steps not 50; 512x512, its smallest supported
Test-Workflow 'lumina2'                           -Overrides @{ steps = 1; width = 512; height = 512 }

# Photanima — 8 steps not 40; 512x512, its smallest supported
Test-Workflow 'photanima'                         -Overrides @{ steps = 1; width = 512; height = 512 }

# Pixel Quantize (video)

# Pixelanima — 8 steps not 40; 512x512, its smallest supported
Test-Workflow 'pixelanima'                        -Overrides @{ steps = 1; width = 512; height = 512 }

# PixelDiT 1300M (pixel-space) — 8 steps not 50; 512x512, its smallest supported
Test-Workflow 'pixeldit'                          -Overrides @{ steps = 1; width = 512; height = 512 }

# Pony Diffusion V6 XL — 8 steps not 25; 512x512, its smallest supported
Test-Workflow 'pony-v6'                           -Overrides @{ steps = 1; width = 512; height = 512 }

# Qwen-Image 2512 — 8 steps not 20; 928x928, its smallest supported
Test-Workflow 'qwen-image-2512'                   -Overrides @{ steps = 1; width = 928; height = 928 }

# Qwen-Image — 8 steps not 20; 928x928, its smallest supported
Test-Workflow 'qwen-image'                        -Overrides @{ steps = 1; width = 928; height = 928 }

# Stable Diffusion 1.5 — 8 steps not 28; 512x512, its smallest supported
Test-Workflow 'sd15'                              -Overrides @{ steps = 1; width = 512; height = 512 }

# Stable Diffusion 2.1 — 8 steps not 25; 512x512, its smallest supported
Test-Workflow 'sd21'                              -Overrides @{ steps = 1; width = 512; height = 512 }

# Stable Diffusion 3.5 Large Turbo — 640x640, its smallest supported
Test-Workflow 'sd35-large-turbo'                  -Overrides @{ steps = 1; width = 640; height = 640 }

# Stable Diffusion 3.5 Large — 8 steps not 30; 640x640, its smallest supported
Test-Workflow 'sd35-large'                        -Overrides @{ steps = 1; width = 640; height = 640 }

# Stable Diffusion 3.5 Medium — 8 steps not 30; 512x512, its smallest supported
Test-Workflow 'sd35-medium'                       -Overrides @{ steps = 1; width = 512; height = 512 }

# SDXL 1.0 — 8 steps not 30; 512x512, its smallest supported
Test-Workflow 'sdxl'                              -Overrides @{ steps = 1; width = 512; height = 512 }

# Wan 2.2 14B — 8 steps not 40; 9 frames not 81; 480x480, its smallest supported
Test-Workflow 'wan22-i2v-a14b-pixel'              -Overrides @{ steps = 1; length = 5; width = 480; height = 480 }

# Wan 2.2 14B 720P — 8 steps not 40; 9 frames not 81; 480x480, its smallest supported
Test-Workflow 'wan22-t2v-a14b-720p'               -Overrides @{ steps = 1; length = 5; width = 480; height = 480 }

# Wan 2.2 14B — 8 steps not 40; 9 frames not 81; 480x480, its smallest supported
Test-Workflow 'wan22-t2v-a14b'                    -Overrides @{ steps = 1; length = 5; width = 480; height = 480 }

# Wan 2.2 T2V — 8 steps not 20; 480x480, its smallest supported
Test-Workflow 'wan22-t2v'                         -Overrides @{ steps = 1; width = 480; height = 480 }

# Wan 2.2 — 8 steps not 50; 9 frames not 121; 704x704, its smallest supported
Test-Workflow 'wan22-ti2v-5b-hq-pixel'            -Overrides @{ steps = 1; length = 5; width = 704; height = 704 }

# Z-Image — NippleDiffusion — 8 steps not 40; 512x512, its smallest supported
Test-Workflow 'z-image-nipplediffusion'           -Overrides @{ steps = 1; width = 512; height = 512 }

# Z-Image Turbo — 512x512, its smallest supported
Test-Workflow 'z-image-turbo'                     -Overrides @{ steps = 1; width = 512; height = 512 }

# Z-Image — 8 steps not 40; 512x512, its smallest supported
Test-Workflow 'z-image'                           -Overrides @{ steps = 1; width = 512; height = 512 }

# --- edit -------------------------------------------------------------------------------------
# Edits run after the generates so each has a real image to work on. -Needs mask uploads a mask
# sized to that image; -Needs pad supplies the extension the frame editor would.

# Anima (inpaint) — 8 steps not 40
Test-Workflow 'anima-inpaint'                     -Edit -Needs mask -Overrides @{ steps = 1; width = 512; height = 512 }

# Anima (outpaint) — 8 steps not 40
Test-Workflow 'anima-outpaint'                    -Edit -Needs pad -Overrides @{ steps = 1; width = 512; height = 512 }

# Anima — 8 steps not 40
Test-Workflow 'anima-redraw'                      -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# AnimateDiff Lightning (SD1.5) — 9 frames not 16; 512x512, its smallest supported
Test-Workflow 'animatediff-lightning-i2v'         -Edit -Overrides @{ steps = 1; length = 8; width = 512; height = 512 }

# AnimateDiff (SD1.5) — 8 steps not 20; 9 frames not 16; 512x512, its smallest supported
Test-Workflow 'animatediff-sd15'                  -Edit -Overrides @{ steps = 1; length = 5; width = 512; height = 512 }

# AnimateLCM (SD1.5) — 9 frames not 16; 512x512, its smallest supported
Test-Workflow 'animatelcm-i2v'                    -Edit -Overrides @{ steps = 1; length = 8; width = 512; height = 512 }

# BiRefNet Matte (image)
Test-Workflow 'birefnet-matte' -Edit

# Boogu-Image Edit — 8 steps not 25
Test-Workflow 'boogu-edit'                        -Edit -Overrides @{ steps = 1; width = 1024; height = 1024 }

# Chroma1-Base — 8 steps not 26
Test-Workflow 'chroma1-base-redraw'               -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# Chroma1-Flash
Test-Workflow 'chroma1-flash-redraw'              -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# Chroma1-HD — 8 steps not 26
Test-Workflow 'chroma1-hd-redraw'                 -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# Chroma1-Radiance (pixel-space) — 8 steps not 26
Test-Workflow 'chroma1-radiance-redraw'           -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# ChronoEdit-14B — 8 steps not 20
Test-Workflow 'chronoedit'                        -Edit -Overrides @{ steps = 1; width = 720; height = 720 }

# DreamOmni2 (reference edit) — 8 steps not 30
Test-Workflow 'dreamomni2-edit'                   -Edit -Overrides @{ steps = 1 }

# FireRed-Image-Edit 1.1 — 8 steps not 40
Test-Workflow 'firered-image-edit'                -Edit -Overrides @{ steps = 1; width = 928; height = 928 }

# FLUX.1-dev — 8 steps not 28
Test-Workflow 'flux1-dev-redraw'                  -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.1 Fill (inpaint) — 8 steps not 20
Test-Workflow 'flux1-fill-inpaint'                -Edit -Needs mask -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.1 Fill (outpaint) — 8 steps not 20
Test-Workflow 'flux1-fill-outpaint'               -Edit -Needs pad -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.1-Kontext — 8 steps not 20
Test-Workflow 'flux1-kontext'                     -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.1-Krea — 8 steps not 28
Test-Workflow 'flux1-krea-redraw'                 -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.1-schnell
Test-Workflow 'flux1-schnell-redraw'              -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.2-dev — 8 steps not 20
Test-Workflow 'flux2-dev-redraw'                  -Edit -Overrides @{ steps = 1; width = 64; height = 64 }

# FLUX.2-Klein 4B Base — 8 steps not 20
Test-Workflow 'flux2-klein-4b-base-redraw'        -Edit -Overrides @{ steps = 1; width = 64; height = 64 }

# FLUX.2-Klein 4B
Test-Workflow 'flux2-klein-4b-edit'               -Edit -Overrides @{ steps = 1; width = 64; height = 64 }

# FLUX.2-Klein 4B
Test-Workflow 'flux2-klein-4b-redraw'             -Edit -Overrides @{ steps = 1; width = 64; height = 64 }

# FLUX.2-Klein 9B
Test-Workflow 'flux2-klein-9b-edit'               -Edit -Overrides @{ steps = 1; width = 64; height = 64 }

# FLUX.2-Klein 9B
Test-Workflow 'flux2-klein-9b-redraw'             -Edit -Overrides @{ steps = 1; width = 64; height = 64 }

# HunyuanVideo (AnimeShots) — 8 steps not 20; 9 frames not 49; 544x544, its smallest supported

# HunyuanVideo (Anime Style) — 8 steps not 20; 9 frames not 49; 544x544, its smallest supported

# HunyuanVideo — 8 steps not 20; 9 frames not 49; 544x544, its smallest supported

# HunyuanVideo 1.5 720p — 8 steps not 20; 9 frames not 49; 480x480, its smallest supported
Test-Workflow 'hunyuanvideo15-720p-i2v'           -Edit -Overrides @{ steps = 1; length = 5; width = 480; height = 480 }

# HunyuanVideo 1.5 HQ (480p) — 8 steps not 30; 9 frames not 49; 320x320, its smallest supported
Test-Workflow 'hunyuanvideo15-i2v-cfg-480'        -Edit -Overrides @{ steps = 1; length = 5; width = 320; height = 320 }

# HunyuanVideo 1.5 HQ (720p) — 8 steps not 30; 9 frames not 49; 480x480, its smallest supported
Test-Workflow 'hunyuanvideo15-i2v-cfg-720'        -Edit -Overrides @{ steps = 1; length = 5; width = 480; height = 480 }

# HunyuanVideo 1.5 Fast — 9 frames not 49; 320x320, its smallest supported
Test-Workflow 'hunyuanvideo15-i2v-fast'           -Edit -Overrides @{ steps = 1; length = 5; width = 320; height = 320 }

# HunyuanVideo 1.5 (1080p SR) — 8 steps not 20; 9 frames not 49; 320x320, its smallest supported
Test-Workflow 'hunyuanvideo15-i2v-sr'             -Edit -Overrides @{ steps = 1; length = 5; width = 320; height = 320 }

# HunyuanVideo 1.5 — 8 steps not 20; 9 frames not 49; 320x320, its smallest supported
Test-Workflow 'hunyuanvideo15-i2v'                -Edit -Overrides @{ steps = 1; length = 5; width = 320; height = 320 }

# Krea 2 Turbo
Test-Workflow 'krea2-redraw'                      -Edit -Overrides @{ steps = 1; width = 1024; height = 1024 }

# Line Thicken (anime line-extract)
Test-Workflow 'line-thicken-anime2sketch' -Edit

# Line Thicken (ControlNet lineart re-render) — 8 steps not 20
Test-Workflow 'line-thicken-controlnet'           -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# Line Thicken (erode)
Test-Workflow 'line-thicken-erode' -Edit

# Line Thicken (sketchKeras)
Test-Workflow 'line-thicken-sketchkeras' -Edit

# Line Thicken (XDoG, outline-only)
Test-Workflow 'line-thicken-xdog' -Edit

# LongCat-Image-Edit Turbo
Test-Workflow 'longcat-image-edit-turbo'          -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# LongCat-Image-Edit — 8 steps not 24
Test-Workflow 'longcat-image-edit'                -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# LTX-2 dev — 8 steps not 30; 9 frames not 97
Test-Workflow 'ltx2-i2v-dev'                      -Edit -Overrides @{ steps = 1; length = 9; width = 128; height = 128 }

# LTX-2 — 9 frames not 97; 32x32, its smallest supported
Test-Workflow 'ltx2-i2v'                          -Edit -Overrides @{ steps = 1; length = 9; width = 128; height = 128 }

# LTX-2.3 22B — 9 frames not 97
Test-Workflow 'ltx23-i2v'                         -Edit -Overrides @{ steps = 1; length = 9; width = 128; height = 128 }

# LTX Video 13B — 9 frames not 97
Test-Workflow 'ltxv-13b-i2v'                      -Edit -Overrides @{ steps = 1; length = 9; width = 128; height = 128 }

# LTX Video — 9 frames not 97; 32x32, its smallest supported
Test-Workflow 'ltxv-i2v'                          -Edit -Overrides @{ steps = 1; length = 9; width = 128; height = 128 }

# Photanima — 8 steps not 40
Test-Workflow 'photanima-redraw'                  -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# Pixel Quantize (batch)
Test-Workflow 'pixel-quantize-batch' -Edit

# Pixel Quantize
Test-Workflow 'pixel-quantize' -Edit

# Anima — 8 steps not 40
Test-Workflow 'pixelize-anima'                    -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# Chroma1-HD — 8 steps not 26
Test-Workflow 'pixelize-chroma'                   -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# DreamOmni2 — 8 steps not 30
Test-Workflow 'pixelize-dreamomni2'               -Edit -Overrides @{ steps = 1 }

# FireRed-Image-Edit 1.1 — 8 steps not 40
Test-Workflow 'pixelize-firered'                  -Edit -Overrides @{ steps = 1; width = 928; height = 928 }

# Flux-dev — 8 steps not 28
Test-Workflow 'pixelize-flux'                     -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.2-dev — 8 steps not 20
Test-Workflow 'pixelize-flux2dev'                 -Edit -Overrides @{ steps = 1; width = 64; height = 64 }

# HiDream-I1 Full — 8 steps not 50
Test-Workflow 'pixelize-hidream'                  -Edit -Overrides @{ steps = 1; width = 768; height = 768 }

# HunyuanImage 2.1 HQ — 8 steps not 50
Test-Workflow 'pixelize-hunyuan'                  -Edit -Overrides @{ steps = 1; width = 1536; height = 1536 }

# FLUX.2-Klein 4B
Test-Workflow 'pixelize-klein4b'                  -Edit -Overrides @{ steps = 1; width = 64; height = 64 }

# FLUX.2-Klein 9B
Test-Workflow 'pixelize-klein9b'                  -Edit -Overrides @{ steps = 1; width = 64; height = 64 }

# FLUX.1-Kontext — 8 steps not 20
Test-Workflow 'pixelize-kontext'                  -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# FLUX.1-Krea — 8 steps not 28
Test-Workflow 'pixelize-krea'                     -Edit -Overrides @{ steps = 1; width = 256; height = 256 }

# Krea 2 — 8 steps not 28
Test-Workflow 'pixelize-krea2'                    -Edit -Overrides @{ steps = 1; width = 1024; height = 1024 }

# LongCat-Image-Edit Turbo
Test-Workflow 'pixelize-longcat-turbo'            -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# LongCat-Image-Edit — 8 steps not 24
Test-Workflow 'pixelize-longcat'                  -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# Lumina-Image 2.0 — 8 steps not 50
Test-Workflow 'pixelize-lumina'                   -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# Qwen-Image-Edit — 8 steps not 20
Test-Workflow 'pixelize-qwen'                     -Edit -Overrides @{ steps = 1; width = 928; height = 928 }

# Stable Diffusion 3.5 Large — 8 steps not 20
Test-Workflow 'pixelize-sd35'                     -Edit -Overrides @{ steps = 1; width = 640; height = 640 }

# Z-Image Turbo
Test-Workflow 'pixelize-zimage-turbo'             -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# Z-Image — 8 steps not 40
Test-Workflow 'pixelize-zimage'                   -Edit -Overrides @{ steps = 1; width = 512; height = 512 }

# Qwen-Image-Edit — 8 steps not 20
Test-Workflow 'qwen-image-edit'                   -Edit -Overrides @{ steps = 1; width = 928; height = 928 }

# Qwen-Image (inpaint) — 8 steps not 20
Test-Workflow 'qwen-image-inpaint'                -Edit -Needs mask -Overrides @{ steps = 1; width = 928; height = 928 }

# Qwen-Image (outpaint) — 8 steps not 20
Test-Workflow 'qwen-image-outpaint'               -Edit -Needs pad -Overrides @{ steps = 1; width = 928; height = 928 }

# Qwen Rapid
Test-Workflow 'qwen-rapid-aio'                    -Edit -Overrides @{ steps = 1; width = 928; height = 928 }

# SDXL AnimateDiff — 8 steps not 20; 9 frames not 16; 512x512, its smallest supported
Test-Workflow 'sdxl-i2v'                          -Edit -Overrides @{ steps = 1; length = 5; width = 512; height = 512 }

# SeedVR2
Test-Workflow 'seedvr2-upscale'                   -Edit -Overrides @{ scale = 1 }

# Step1X-Edit (i1258) — 8 steps not 28
Test-Workflow 'step1x-edit-i1258'                 -Edit -Overrides @{ steps = 1; width = 256 }

# Anime
Test-Workflow 'upscale-anime'                     -Edit -Overrides @{ scale = 1 }

# Photo
Test-Workflow 'upscale-photo'                     -Edit -Overrides @{ scale = 1 }

# Wan 2.2 (Flat Color) — 8 steps not 50; 9 frames not 121; 704x704, its smallest supported
Test-Workflow 'wan-anime-flatcolor'               -Edit -Overrides @{ steps = 1; length = 5; width = 704; height = 704 }

# Wan 2.2 (Anime LoRA) — 8 steps not 50; 9 frames not 121; 704x704, its smallest supported
Test-Workflow 'wan-anime-test'                    -Edit -Overrides @{ steps = 1; length = 5; width = 704; height = 704 }

# Wan 2.2 14B 720P — 8 steps not 40; 9 frames not 81; 480x480, its smallest supported
Test-Workflow 'wan22-i2v-a14b-720p'               -Edit -Overrides @{ steps = 1; length = 5; width = 480; height = 480 }

# Wan 2.2 14B — 8 steps not 40; 9 frames not 81; 480x480, its smallest supported
Test-Workflow 'wan22-i2v-a14b'                    -Edit -Overrides @{ steps = 1; length = 5; width = 480; height = 480 }

# Wan 2.2 — 8 steps not 50; 9 frames not 121; 704x704, its smallest supported
Test-Workflow 'wan22-ti2v-5b'                     -Edit -Overrides @{ steps = 1; length = 5; width = 704; height = 704 }

# These three consume a CLIP, so they run after the generators rather than in catalogue order --
# placed earlier they would find no video and skip, which is how three of the 147 went untested.
Test-Workflow 'birefnet-matte-video'
Test-Workflow 'deflicker-auto'
Test-Workflow 'pixel-quantize-video'

# --- report -----------------------------------------------------------------------------------

# An edit block that ran before anything generated has no source. Rather than leave that as a hole,
# fall back to an image already in the library -- the same query the gallery makes -- and say so.
# Only worth saying when nothing usable was produced AT ALL. A run that generated a clip but no still
# has a source, and claiming otherwise contradicts the edits that plainly just used one.
if (-not $script:SourceImage -and -not $script:SourceVideo) {
    Write-Meh "nothing generated: edits had no source of this run's own making"
}

$results = $script:Results
Write-Step "Result"

<#
  A run that exercised nothing is a failure, not a pass. Exiting 0 having tested zero configurations is
  indistinguishable from "everything worked" to anything reading the exit code, and it is exactly what a
  mistyped -Only produces.
#>
if (-not $results -or @($results).Count -eq 0) {
    Write-Bad "no configuration ran"
    if ($script:Only) { Write-Bad "-Only matched none of the blocks: $($script:Only -join ', ')" }
    exit 2
}

foreach ($g in ($results | Group-Object Status | Sort-Object Count -Descending)) {
    $colour = if ($g.Name -eq 'done') { 'Green' } elseif ($g.Name -in 'skipped', 'unavailable') { 'DarkGray' } else { 'Red' }
    Write-Host ("   {0,-14} {1}" -f $g.Name, $g.Count) -ForegroundColor $colour
}

# Three different things share the 'unavailable' status, and only one of them is about THIS script: a block
# naming a configuration the catalogue does not have is a stale line here, not a property of the build being
# tested. Lumped in with "hidden" and "not runnable" it reads as the machine's shortcoming and stays invisible
# -- sd35-large-bf16 sat in this file for a whole sweep after #67 removed the configuration. Called out
# separately for the same reason the slot-count mismatch above is: a harness that quietly tests nothing is the
# failure this file exists to prevent.
$ghosts = @($results | Where-Object { $_.Status -eq 'unavailable' -and $_.Error -eq 'not in this build''s catalogue' })
if ($ghosts.Count) {
    Write-Host ""
    Write-Bad "$($ghosts.Count) block(s) name a configuration that does not exist — stale lines in this script:"
    foreach ($g in $ghosts) { Write-Bad "     $($g.Id)" }
}

$failures = @($results | Where-Object { $_.Status -notin 'done', 'unavailable', 'skipped' })
if ($failures.Count) {
    Write-Host ""
    Write-Host "   Failures, in the order they need looking at:" -ForegroundColor Yellow
    foreach ($f in $failures) { Write-Host ("     {0,-34} {1,-12} {2}" -f $f.Id, $f.Status, $f.Error) }
}

$report = [pscustomobject]@{
    ranAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    baseUrl  = $script:Root
    account  = $script:Account
    binding  = $binding
    results  = $results
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ReportPath -Encoding utf8
Write-Host ""
Write-Ok "report written to $ReportPath"

if ($failures.Count) { exit 1 } else { exit 0 }
