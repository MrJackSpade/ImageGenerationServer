<#
  export-comfy-patches.ps1
  --------------------------------------------------------------------------
  Re-exports the diff bodies in comfy-patches\ from a live ComfyUI checkout.

  Each .patch file is a metadata header, a line reading exactly "---", and then a unified
  diff. This script rewrites ONLY the diff, from what is on disk right now; the header is
  the source of truth for what to diff against and is preserved untouched:

      Target: .                     -> the working-tree diff of the files the patch touches
      Target: custom_nodes\<pack>   -> git diff <Rev>..working-tree, inside that pack

  Dry run by default -- it prints what would change and writes nothing. That default is not
  politeness: the shipped patches carry corrections the live checkout does NOT have (absolute
  model paths resolved through folder_paths, one machine's memory budget dropped). Exporting
  over them without looking would put all of that back. Reconcile the checkout first --
  Settings -> Renderer patches, or the ComfyPatch tool -- and then export.

  Usage:
      .\scripts\export-comfy-patches.ps1 -ComfyRoot F:\AI\ComfyUI            # show what differs
      .\scripts\export-comfy-patches.ps1 -ComfyRoot F:\AI\ComfyUI -Apply     # rewrite the bodies
#>
param(
    [Parameter(Mandatory = $true)][string] $ComfyRoot,
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'

$patchDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'comfy-patches'
if (-not (Test-Path $patchDir)) { throw "No patch directory: $patchDir" }
if (-not (Test-Path (Join-Path $ComfyRoot 'main.py'))) { throw "Not a ComfyUI checkout (no main.py): $ComfyRoot" }

# Parse the header the same way the app does: "Key: value", continuation lines start with a space,
# and a line of exactly "---" ends the header. Everything after it is the diff.
function Read-Patch($path) {
    $lines = [System.IO.File]::ReadAllLines($path)
    $head = @{}; $key = $null; $i = 0
    for (; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -eq '---') { $i++; break }
        if ($line.StartsWith(' ') -and $key) { $head[$key] += "`n" + $line.Substring(1); continue }
        $colon = $line.IndexOf(':')
        if ($colon -lt 1) { throw "$([System.IO.Path]::GetFileName($path)): line $($i+1) is neither a header nor '---'." }
        $key = $line.Substring(0, $colon).Trim()
        $head[$key] = $line.Substring($colon + 1).Trim()
    }
    [pscustomobject]@{
        Header = ($lines[0..($i - 1)] -join "`n")
        Fields = $head
        Body   = if ($i -lt $lines.Count) { ($lines[$i..($lines.Count - 1)] -join "`n") } else { '' }
    }
}

# git writes CRLF on Windows when core.autocrlf is on; the stored patches are LF and are compared
# byte for byte, so normalise here rather than shipping a file that differs by platform.
function Invoke-Git($workDir, [string[]] $gitArgs) {
    $out = & git -C $workDir @gitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($gitArgs -join ' ') failed in ${workDir}: $out" }
    (($out -join "`n") -replace "`r`n", "`n")
}

$changed = @(); $same = 0

foreach ($file in Get-ChildItem -Path $patchDir -Filter '*.patch' | Sort-Object Name) {
    $patch  = Read-Patch $file.FullName
    $target = $patch.Fields['Target']
    if (-not $target) { throw "$($file.Name) has no Target: header." }

    if ($target -eq '.') {
        $workDir = $ComfyRoot
        # The files this patch already touches -- diffing the whole checkout would sweep in
        # everything else uncommitted, which is not what this patch is.
        $paths = [regex]::Matches($patch.Body, '(?m)^\+\+\+ b/(.+)$') | ForEach-Object { $_.Groups[1].Value }
        if (-not $paths) { throw "$($file.Name) names no files." }
        $body = Invoke-Git $workDir (@('diff', '--') + $paths)
    }
    else {
        $workDir = Join-Path $ComfyRoot $target
        $rev = $patch.Fields['Rev']
        if (-not $rev) { throw "$($file.Name) targets a pack but has no Rev: header to diff against." }
        if (-not (Test-Path $workDir)) { Write-Host "  SKIP  $($file.Name) -- $target is not installed"; continue }
        $body = Invoke-Git $workDir @('diff', $rev)
    }

    if ($body -eq $patch.Body.TrimEnd("`n")) { $same++; continue }

    $changed += $file.Name
    Write-Host "  DIFFERS  $($file.Name)"
    if ($Apply) {
        $text = $patch.Header + "`n---`n" + $body + "`n"
        [System.IO.File]::WriteAllText($file.FullName, ($text -replace "`r`n", "`n"))
    }
}

Write-Host ''
if ($changed.Count -eq 0) {
    Write-Host "In sync -- $same patch(es) already match $ComfyRoot."
    exit 0
}

if ($Apply) {
    Write-Host ("Rewrote {0} patch body/bodies. Read the diff before committing: the checkout is the input here, so" -f $changed.Count)
    Write-Host "anything local and unreconciled has just been written into the shipped patch set."
} else {
    Write-Host ("{0} patch(es) differ from {1}. Dry run -- nothing written." -f $changed.Count, $ComfyRoot)
    Write-Host "Re-run with -Apply only if the checkout is what you want shipped."
}
