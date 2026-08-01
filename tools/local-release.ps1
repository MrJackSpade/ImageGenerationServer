#requires -Version 7.0
<#
  local-release.ps1
  --------------------------------------------------------------------------
  Puts a real release archive on disk, unpacked, so changes can be tested against the payload users get.

  It does NOT build anything itself. It RUNS .github/workflows/release.yml on GitHub Actions and unpacks that
  run's artifact. Reproducing the workflow's steps locally is how the two drift: every step re-implemented here
  is a step that can stop matching without anything failing, and then what is tested is not what ships. The only
  way the output cannot disagree with the release is for it to BE the release.

  It pushes the current branch and moves a tag, because the workflow builds a ref on the remote -- it cannot see
  a working tree. The workflow's publish job then creates a GitHub release for that tag; the tag is a throwaway
  and the script recycles it on every run.

  Usage:
      .\tools\local-release.ps1
      .\tools\local-release.ps1 -Tag v0.0.0-test -Rid linux-x64 -Root D:\somewhere
#>
[CmdletBinding()]
param(
    [string] $Root = 'E:\AI\imagegen-test',

    # The throwaway tag the workflow builds. Recycled every run, so this is not release history.
    [string] $Tag  = 'v0.0.0-test',

    [ValidateSet('win-x64', 'linux-x64')]
    [string] $Rid  = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent

function Head ($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Ok   ($t) { Write-Host "   $t" -ForegroundColor Green }
function Note ($t) { Write-Host "   $t" }

if (-not (Get-Command gh -EA SilentlyContinue)) { throw 'the GitHub CLI (gh) is required to run the workflow.' }
& gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'gh is not authenticated. Run: gh auth login' }

# An uncommitted change is invisible to the workflow, so a run against a dirty tree tests something that is not
# what is on disk -- silently, and looking exactly like a run that worked.
if (& git -C $RepoRoot status --porcelain) { throw 'the working tree is dirty. The workflow builds a pushed ref, so commit first.' }

$branch   = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD).Trim()
# The FULL sha: gh reports headSha as the full 40 characters, so matching against the abbreviated form never
# equals it -- the reuse check silently missed every already-built run. The short form is kept only for display.
$sha      = (& git -C $RepoRoot rev-parse HEAD).Trim()
$shortSha = (& git -C $RepoRoot rev-parse --short HEAD).Trim()

# The run for this tag AT THIS COMMIT, if the build has already happened. Re-running the script must not mean
# re-running a six-minute build to fetch bytes that already exist. The selection is done here in PowerShell over
# gh's JSON, not in a jq filter passed to gh: that filter had to carry literal quotes across two argument parsers
# and lost, reducing to `\"` -- syntax gojq rejects -- so every call errored to nothing and nothing was ever reused.
function Find-Run ($tag, $sha) {
    $runs = & gh run list --workflow release.yml --limit 20 `
        --json databaseId,headBranch,headSha,status,conclusion | ConvertFrom-Json
    ($runs | Where-Object {
        $_.headBranch -eq $tag -and $_.headSha -eq $sha -and $_.conclusion -eq 'success'
    } | Select-Object -First 1).databaseId
}

$runId = Find-Run $Tag $sha
if ($runId) {
    Head "Reusing run $runId"
    Ok "$Tag is already built at $shortSha"
}
else {
    Head "Publishing $branch ($shortSha) as $Tag"
    & git -C $RepoRoot push origin $branch
    if ($LASTEXITCODE -ne 0) { throw 'push failed' }

    # Recycled: the publish job creates a release and would fail on a name that already exists, and the tag has
    # to point at THIS commit for the build to be of this code.
    & gh release delete $Tag --repo (& gh repo view --json nameWithOwner -q .nameWithOwner) --yes --cleanup-tag 2>&1 | Out-Null
    & git -C $RepoRoot tag -f $Tag | Out-Null

    <#
      Pushing the tag IS the trigger -- release.yml runs on push of `v*`. It was also dispatched explicitly here,
      which started a SECOND build of the same commit; the two raced to `gh release create v0.0.0-test`, the
      loser failed, and the script happened to watch the loser and reported a failed release for a build that
      had succeeded. One trigger, and wait for the run that trigger created.
    #>
    Head 'Running release.yml on GitHub Actions'
    & git -C $RepoRoot push -f origin "refs/tags/$Tag"
    if ($LASTEXITCODE -ne 0) { throw 'tag push failed' }

    while (-not $runId) {
        $runs = & gh run list --workflow release.yml --limit 20 `
            --json databaseId,headBranch,headSha,event | ConvertFrom-Json
        $runId = ($runs | Where-Object {
            $_.headBranch -eq $Tag -and $_.headSha -eq $sha -and $_.event -eq 'push'
        } | Select-Object -First 1).databaseId
        if (-not $runId) { Start-Sleep -Seconds 2 }
    }
    Ok "run $runId"
    & gh run watch $runId --exit-status
    if ($LASTEXITCODE -ne 0) { throw "the workflow failed. See: gh run view $runId --log-failed" }
}

Head "Unpacking the $Rid artifact"
$staging = Join-Path ([IO.Path]::GetTempPath()) "imagegen-artifact-$runId"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
& gh run download $runId -n $Rid -D $staging
if ($LASTEXITCODE -ne 0) { throw "could not download the $Rid artifact from run $runId" }

# The archive contains a single `imagegen` directory -- that IS the release layout, so it is what lands.
$archive = Get-ChildItem $staging -File | Select-Object -First 1
if (-not $archive) { throw "run $runId produced no archive" }

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Root | Out-Null
if ($archive.Extension -eq '.zip') { Expand-Archive $archive.FullName -DestinationPath $Root -Force }
else { & tar -xzf $archive.FullName -C $Root }
Remove-Item $staging -Recurse -Force

$payload = Join-Path $Root 'imagegen'
Head 'Ready'
Ok "$($archive.Name) unpacked to $payload"
Note "Root:  $((Get-ChildItem $payload -File | Select-Object -ExpandProperty Name) -join ', ')"
Note "Start it the way a user does: $(Join-Path $payload 'start.bat')"
