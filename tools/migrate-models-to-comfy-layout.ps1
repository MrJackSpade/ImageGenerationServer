#requires -Version 7.0
<#
  migrate-models-to-comfy-layout.ps1
  --------------------------------------------------------------------------
  Moves E:\AI\models from the A1111/forge-neo layout it inherited to ComfyUI's OWN layout, and then
  deletes the translation file that existed only to bridge the two.

  Why this exists. The container mounts your models straight onto ComfyUI's native root
  (compose.yml: "${COMFY_MODELS_DIR}:/opt/ComfyUI/models"), so it assumes ComfyUI's directory names.
  This box uses A1111 names, and `extra_model_paths.yaml` translates between them -- a file that lives
  on exactly one disk, ships nowhere, and that nothing tests. Every model-path fact learned here gets
  written into it and is then invisible to the image. The fix is not to ship the translation; it is to
  stop needing one. After this runs, the box and the container are the same arrangement: ComfyUI's
  layout at ComfyUI's path, reached by a junction here and by a bind mount there.

  Nothing is copied. Every move is within E:, so each one is a rename and completes at once regardless
  of the 680 GB in Stable-diffusion.

  Bindings survive. The app binds a slot to a FILENAME, never to a path, so the database needs no
  migration and no re-binding after this.

  Run it with no switches first: that reports exactly what it would do and changes nothing. Add
  -Execute to apply.

  Usage:
      .\tools\migrate-models-to-comfy-layout.ps1                # report only
      .\tools\migrate-models-to-comfy-layout.ps1 -Execute       # apply
#>
[CmdletBinding()]
param(
    [string] $ModelsRoot  = 'E:\AI\models',
    [string] $ComfyRoot   = 'F:\AI\ComfyUI',
    [string] $CatalogRoot = "$PSScriptRoot\..\configurations\models",

    # Apply the plan. Without this the script only reports.
    [switch] $Execute
)

$ErrorActionPreference = 'Stop'

function Say  ($t) { Write-Host $t }
function Head ($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }
function Ok   ($t) { Write-Host "   $t" -ForegroundColor Green }
function Warn ($t) { Write-Host "   $t" -ForegroundColor Yellow }
function Die  ($t) { Write-Host ""; Write-Host "STOPPED: $t" -ForegroundColor Red; exit 1 }

# --- preflight ----------------------------------------------------------------------------------

Head "Preflight"

if (-not (Test-Path $ModelsRoot))  { Die "No models root at $ModelsRoot." }
if (-not (Test-Path $ComfyRoot))   { Die "No ComfyUI at $ComfyRoot." }
if (-not (Test-Path $CatalogRoot)) { Die "No model catalogue at $CatalogRoot." }

<#
  ComfyUI must be stopped BEFORE ANYTHING MOVES. It holds open handles under models/, and the last step
  REPLACES $ComfyRoot\models with a junction, which cannot happen underneath a running process. This
  only checks; it never stops anything, because killing a renderer mid-render is not this script's call.

  It is not a reason to refuse the REPORT. Reading headers and printing a plan changes nothing, and
  being unable to see the plan until the renderer is down is exactly backwards.
#>
$live = $null
try { $live = Invoke-WebRequest 'http://127.0.0.1:8188/system_stats' -UseBasicParsing } catch { }
if ($live -and $Execute) { Die "ComfyUI is answering on :8188. Stop it, then run this again." }
if ($live) { Warn "ComfyUI is running - fine for this report, but it must be stopped before -Execute" }
else       { Ok "ComfyUI is not running" }

# Everything moves within one volume, so a rename is instant. Across volumes it would be a 680 GB copy,
# which is a different operation with different risks -- refuse rather than silently do that instead.
if ((Split-Path -Qualifier $ModelsRoot) -ne (Split-Path -Qualifier (Split-Path $ModelsRoot))) {
    Die "Expected $ModelsRoot to sit on one volume."
}
Ok "all moves are same-volume renames, nothing is copied"

# --- what the catalogue says a file IS ------------------------------------------------------------

<#
  The one judgement in this migration: Stable-diffusion currently backs THREE ComfyUI keys
  (checkpoints, diffusion_models, unet), and ComfyUI keeps checkpoints and diffusion models in separate
  directories. So its 69 files have to be split.

  A checkpoint carries the whole pipeline -- denoiser, autoencoder and text encoder in one file. A
  diffusion model carries only the denoiser. That difference is written into the file itself, so it is
  read from the file rather than guessed from the name: safetensors stores a JSON header of tensor
  names, and a checkpoint's names include the autoencoder and conditioner trees.

  GGUF is diffusion-model-only by construction; there is no such thing as a GGUF full checkpoint here.
#>
$CheckpointMarkers = @('first_stage_model.', 'cond_stage_model.', 'conditioner.', 'text_encoders.', 'vae.')

function Get-SafetensorsTensorNames {
    param([Parameter(Mandatory)][string] $Path)

    $fs = [IO.File]::OpenRead($Path)
    try {
        $lenBytes = [byte[]]::new(8)
        if ($fs.Read($lenBytes, 0, 8) -ne 8) { throw "unreadable safetensors header" }
        $len = [BitConverter]::ToUInt64($lenBytes, 0)

        $json = [byte[]]::new($len)
        $read = 0
        while ($read -lt $len) {
            $n = $fs.Read($json, $read, $len - $read)
            if ($n -le 0) { throw "safetensors header ended after $read of $len bytes" }
            $read += $n
        }
        return ([Text.Encoding]::UTF8.GetString($json) | ConvertFrom-Json).PSObject.Properties.Name
    }
    finally { $fs.Dispose() }
}

# Only these are weights. Anything else in the folder (ComfyUI's own "Put Checkpoint here.txt", stray
# notes, .json sidecars) is not a model, has no correct destination, and is left exactly where it is.
$WeightExtensions = @('.safetensors', '.gguf', '.ckpt', '.pt', '.pth', '.bin')

function Get-ModelClass {
    param([Parameter(Mandatory)][IO.FileInfo] $File)

    switch ($File.Extension.ToLowerInvariant()) {
        '.gguf' { return 'diffusion_models' }
        '.safetensors' {
            $names = Get-SafetensorsTensorNames $File.FullName
            foreach ($marker in $CheckpointMarkers) {
                if ($names | Where-Object { $_.StartsWith($marker, [StringComparison]::OrdinalIgnoreCase) }) {
                    return 'checkpoints'
                }
            }
            return 'diffusion_models'
        }
        # A .ckpt/.pt/.bin is a pickle: classifying it means executing it, which this will not do.
        default { return $null }
    }
}

<#
  The catalogue's own answer for the files it knows, used to CHECK the classifier rather than to
  replace it. configurations/models/<id>.json declares each slot's kind; tools/ui-smoke.ps1 names the
  file for each slot. Where both exist and disagree with the file's own header, something is wrong that
  a 680 GB move should not be built on top of.
#>
function Get-CatalogueExpectations {
    $kindBySlot = @{}
    foreach ($f in Get-ChildItem $CatalogRoot -Filter *.json) {
        $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
        $kindBySlot[[string]$j.id] = [string]$j.kind
    }

    $expected = @{}
    $smoke = Join-Path $PSScriptRoot 'ui-smoke.ps1'
    if (Test-Path $smoke) {
        foreach ($m in [regex]::Matches((Get-Content $smoke -Raw), "(?m)^Bind\s+'([^']+)'\s+'([^']+)'")) {
            $slot = $m.Groups[1].Value; $file = $m.Groups[2].Value
            if (-not $kindBySlot.ContainsKey($slot)) { continue }
            switch ($kindBySlot[$slot]) {
                'checkpoint' { $expected[$file] = 'checkpoints' }
                'unet'       { $expected[$file] = 'diffusion_models' }
            }
        }
    }
    return $expected
}

Head "Classifying Stable-diffusion"

$sd = Join-Path $ModelsRoot 'Stable-diffusion'
if (-not (Test-Path $sd)) { Die "No $sd -- has this already been migrated?" }

$expected = Get-CatalogueExpectations
$plan = @(); $unknown = @(); $conflicts = @(); $locked = @(); $notWeights = @()

foreach ($file in Get-ChildItem $sd -File) {
    if ($file.Extension.ToLowerInvariant() -notin $WeightExtensions) { $notWeights += $file.Name; continue }

    # A file something else has open cannot be read and must not be moved. Said plainly, because
    # "cannot classify" would send you looking at the wrong thing entirely.
    $class = $null
    try { $class = Get-ModelClass $file }
    catch [IO.IOException] { $locked += $file.Name; continue }

    if (-not $class) { $unknown += $file.Name; continue }

    if ($expected.ContainsKey($file.Name) -and $expected[$file.Name] -ne $class) {
        $conflicts += "$($file.Name): the file reads as $class, the catalogue calls it $($expected[$file.Name])"
    }
    $plan += [pscustomobject]@{ File = $file; Class = $class }
}

$nCheck = @($plan | Where-Object Class -eq 'checkpoints').Count
$nDiff  = @($plan | Where-Object Class -eq 'diffusion_models').Count
Ok "$nCheck checkpoint(s), $nDiff diffusion model(s)"

foreach ($n in $notWeights) { Warn "not a model, staying put: $n" }
foreach ($l in $locked)     { Warn "open by another process: $l" }
foreach ($u in $unknown)    { Warn "cannot classify: $u" }
foreach ($c in $conflicts)  { Warn "disagreement: $c" }

# None of these is guessed past when it comes time to MOVE anything. A locked file is a download or a
# renderer still working; an unclassifiable one has nowhere correct to go; a file whose header
# contradicts the catalogue means one of the two is wrong. Moving 680 GB on top of any of them is how a
# wrong answer becomes permanent. They do not stop the report, which exists to show you exactly this.
if ($Execute) {
    if ($locked.Count)    { Die "$($locked.Count) file(s) are open elsewhere. Let them finish, then run this again." }
    if ($unknown.Count)   { Die "$($unknown.Count) file(s) in Stable-diffusion cannot be classified." }
    if ($conflicts.Count) { Die "$($conflicts.Count) file(s) contradict the catalogue." }
}

# --- the plan -----------------------------------------------------------------------------------

<#
  Directory renames. ComfyUI's names are lowercase and pluralised; Windows compares names without case,
  so VAE -> vae and ControlNet -> controlnet cannot be done in one step and go via a temporary name.

  Left alone deliberately: animatediff_models, clip_vision, diffusers, embeddings, ipadapter and
  latent_upscale_models already carry ComfyUI's names; llm, HunyuanImage-3-NF4-v2 and SEEDVR2 are not
  ComfyUI folder keys at all but model directories their own nodes open by name.
#>
$renames = [ordered]@{
    'VAE'          = 'vae'
    'text_encoder' = 'text_encoders'
    'Lora'         = 'loras'
    'ControlNet'   = 'controlnet'
    'ESRGAN'       = 'upscale_models'
}

Head "Plan"
Say "   split   Stable-diffusion -> checkpoints ($nCheck) + diffusion_models ($nDiff)"
foreach ($from in $renames.Keys) {
    $src = Join-Path $ModelsRoot $from
    if (Test-Path $src) { Say "   rename  $from -> $($renames[$from])" }
    else                { Say "   rename  $from -> $($renames[$from])   (absent, skipped)" }
}
Say "   retire  $ComfyRoot\extra_model_paths.yaml"
Say "   junction $ComfyRoot\models -> $ModelsRoot"

if (-not $Execute) {
    Write-Host ""
    Warn "Report only. Re-run with -Execute to apply."
    Write-Host ""
    Say "   Files that would move to checkpoints:"
    $plan | Where-Object Class -eq 'checkpoints' | ForEach-Object { Say "     $($_.File.Name)" }
    exit 0
}

# --- apply --------------------------------------------------------------------------------------

Head "Applying"

# Refuse to merge into an existing directory: a half-finished earlier run is not something to guess at.
# A case-only rename is NOT a collision, though -- Windows compares names without case, so VAE and vae are one
# directory and `Test-Path vae` is true precisely BECAUSE the source is sitting there. Only a destination that
# is a genuinely different directory counts.
foreach ($target in @('checkpoints', 'diffusion_models')) {
    $path = Join-Path $ModelsRoot $target
    if (Test-Path $path) { Die "$path already exists. Resolve it by hand; this script will not merge into it." }
}
foreach ($from in $renames.Keys) {
    $to = $renames[$from]
    if ($from -ieq $to) { continue }
    $path = Join-Path $ModelsRoot $to
    if (Test-Path $path) { Die "$path already exists. Resolve it by hand; this script will not merge into it." }
}

foreach ($target in @('checkpoints', 'diffusion_models')) {
    New-Item -ItemType Directory (Join-Path $ModelsRoot $target) | Out-Null
}
foreach ($item in $plan) {
    Move-Item $item.File.FullName (Join-Path $ModelsRoot $item.Class)
}
Ok "split $($plan.Count) file(s) out of Stable-diffusion"

$leftover = @(Get-ChildItem $sd -Force)
if ($leftover.Count) { Warn "$sd still holds $($leftover.Count) item(s); left in place" }
else { Remove-Item $sd; Ok "removed the empty Stable-diffusion" }

foreach ($from in $renames.Keys) {
    $src = Join-Path $ModelsRoot $from
    if (-not (Test-Path $src)) { continue }
    $to = $renames[$from]
    # Case-only renames need the intermediate step; the others take it harmlessly.
    $tmp = Join-Path $ModelsRoot "$to.migrating"
    Move-Item $src $tmp
    Move-Item $tmp (Join-Path $ModelsRoot $to)
    Ok "$from -> $to"
}

<#
  SeedVR2's autoencoder STAYS in the pack's own folder. It is not a ComfyUI VAE: nothing loads it through
  VAELoader, and the catalogue's seedvr2-vae slot is kind `seedvr2`, served by the pack's own loaders.
  Moving it into vae/ would have hidden it from the pack -- and not finding it is exactly what makes
  SeedVR2 download a fresh copy.
#>

<#
  The translation file goes. It is kept next to where it lived rather than deleted, because it is the
  only record of how this box used to be laid out and it is tracked nowhere -- but it is renamed so
  ComfyUI cannot read it, since a stale mapping pointing at directories that no longer exist is worse
  than none.
#>
$yaml = Join-Path $ComfyRoot 'extra_model_paths.yaml'
if (Test-Path $yaml) {
    Move-Item $yaml "$yaml.pre-comfy-layout"
    Ok "retired extra_model_paths.yaml (kept as .pre-comfy-layout)"
}

<#
  Finally ComfyUI's own models directory becomes this one. After this there is no mapping anywhere:
  ComfyUI looks where it always looks, and finds the layout it always expected. The container reaches
  the identical arrangement through its bind mount, so the two stop being able to drift.
#>
$comfyModels = Join-Path $ComfyRoot 'models'
if (Test-Path $comfyModels) {
    $weights = @(Get-ChildItem $comfyModels -Recurse -File -Include *.safetensors, *.gguf, *.ckpt, *.pth, *.pt, *.bin -EA SilentlyContinue)
    if ($weights.Count) {
        Warn "$comfyModels still holds $($weights.Count) weight file(s):"
        $weights | ForEach-Object { Warn "     $($_.FullName)" }
        Die "Move those onto $ModelsRoot first. This script will not decide what happens to them."
    }
    Remove-Item $comfyModels -Recurse -Force
}
& cmd /c mklink /J "$comfyModels" "$ModelsRoot" | Out-Null
if (-not (Test-Path $comfyModels)) { Die "The junction was not created." }
Ok "$comfyModels -> $ModelsRoot"

Head "Done"
Say "   Start ComfyUI, then check the models page: every binding is by filename, so nothing should"
Say "   have come unbound. Set COMFY_MODELS_DIR=$ModelsRoot in .env and the container now sees the"
Say "   same layout this box has."
