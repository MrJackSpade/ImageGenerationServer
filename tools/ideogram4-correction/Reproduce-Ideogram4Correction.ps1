#Requires -Version 5.1

<#
.SYNOPSIS
Builds an isolated, pinned Ideogram 4 correction environment and runs paired generations.

.DESCRIPTION
The script performs a fail-fast preflight before downloading anything, installs an isolated
Python/ComfyUI runtime, verifies all model and correction artifacts by SHA-256, applies the
small reversible ComfyUI hook, and runs baseline/zero-strength/corrected comparisons.

It never edits an existing ComfyUI installation or a checkpoint. The temporary ComfyUI server
is bound to localhost and is stopped in a finally block, including failed runs.

.EXAMPLE
.\Reproduce-Ideogram4Correction.ps1 -PreflightOnly

.EXAMPLE
.\Reproduce-Ideogram4Correction.ps1 -AcceptModelLicense

Uses the prompts and seeds in reproduction.config.psd1. Edit that file instead of constructing
a long command line.

.EXAMPLE
.\Reproduce-Ideogram4Correction.ps1 -Mode FullValidation -AcceptModelLicense

.EXAMPLE
.\Reproduce-Ideogram4Correction.ps1 -Mode Custom -PromptFile .\prompts.txt `
    -Seed 12345,67890 -AcceptModelLicense
#>

[CmdletBinding()]
param(
    # The adjacent .psd1 file is the easiest place to edit prompts, seeds, paths, and mode.
    # Explicit command-line parameters always take precedence over values in that file.
    [Parameter()]
    [string] $ConfigFile = (Join-Path $PSScriptRoot 'reproduction.config.psd1'),

    [Parameter()]
    [string] $Root = (Join-Path (Get-Location) 'ideogram4-correction-reproduction-v4'),

    [Parameter()]
    [ValidateSet('Smoke', 'FullValidation', 'Custom')]
    [string] $Mode = 'Smoke',

    [Parameter()]
    [string[]] $Prompt,

    [Parameter()]
    [string] $PromptFile,

    [Parameter()]
    [long[]] $Seed,

    [Parameter()]
    [ValidateRange(0, 31)]
    [int] $GpuIndex = 0,

    [Parameter()]
    [ValidateRange(1024, 65535)]
    [int] $Port = 8194,

    [Parameter()]
    [string] $ExistingModelRoot,

    [Parameter()]
    [string] $HuggingFaceToken = $env:HF_TOKEN,

    [Parameter()]
    [switch] $AcceptModelLicense,

    [Parameter()]
    [switch] $AllowUntestedVram,

    [Parameter()]
    [switch] $PreflightOnly,

    [Parameter()]
    [switch] $SetupOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Net.Http

# =============================================================================
# USER CONFIGURATION
# =============================================================================
# PowerShell data files contain values only (no executable setup logic). This makes the
# reproduction.config.psd1 file safe to inspect and much easier to edit than this bootstrap.
# A command-line argument wins when both the command line and config file specify a value.
if (-not (Test-Path -LiteralPath $ConfigFile -PathType Leaf)) {
    throw "Configuration file does not exist: $ConfigFile"
}
$configurationPath = [System.IO.Path]::GetFullPath($ConfigFile)
$configurationDirectory = Split-Path -Parent $configurationPath
$configuration = Import-PowerShellDataFile -LiteralPath $configurationPath
$allowedConfigurationKeys = @(
    'Mode', 'Prompts', 'PromptFile', 'Seeds', 'Root', 'GpuIndex', 'Port', 'ExistingModelRoot'
)
foreach ($key in $configuration.Keys) {
    if ($allowedConfigurationKeys -notcontains [string]$key) {
        throw "Unknown key '$key' in $configurationPath"
    }
}

if (-not $PSBoundParameters.ContainsKey('Mode') -and $configuration.ContainsKey('Mode')) {
    $Mode = [string]$configuration.Mode
}
if (@('Smoke', 'FullValidation', 'Custom') -notcontains $Mode) {
    throw "Mode must be Smoke, FullValidation, or Custom; found '$Mode'."
}
if (-not $PSBoundParameters.ContainsKey('Root') -and
    $configuration.ContainsKey('Root') -and
    -not [string]::IsNullOrWhiteSpace([string]$configuration.Root)) {
    $configuredRoot = [string]$configuration.Root
    $Root = if ([System.IO.Path]::IsPathRooted($configuredRoot)) {
        $configuredRoot
    }
    else {
        Join-Path $configurationDirectory $configuredRoot
    }
}
if (-not $PSBoundParameters.ContainsKey('GpuIndex') -and $configuration.ContainsKey('GpuIndex')) {
    $GpuIndex = [int]$configuration.GpuIndex
}
if ($GpuIndex -lt 0 -or $GpuIndex -gt 31) {
    throw "GpuIndex must be between 0 and 31; found $GpuIndex."
}
if (-not $PSBoundParameters.ContainsKey('Port') -and $configuration.ContainsKey('Port')) {
    $Port = [int]$configuration.Port
}
if ($Port -lt 1024 -or $Port -gt 65535) {
    throw "Port must be between 1024 and 65535; found $Port."
}
if (-not $PSBoundParameters.ContainsKey('ExistingModelRoot') -and
    $configuration.ContainsKey('ExistingModelRoot') -and
    -not [string]::IsNullOrWhiteSpace([string]$configuration.ExistingModelRoot)) {
    $configuredModelRoot = [string]$configuration.ExistingModelRoot
    $ExistingModelRoot = if ([System.IO.Path]::IsPathRooted($configuredModelRoot)) {
        $configuredModelRoot
    }
    else {
        Join-Path $configurationDirectory $configuredModelRoot
    }
}

# Prompt settings from the config are intentionally ignored for Smoke and FullValidation.
# Those modes use frozen cases; Custom mode uses the editable list below or CLI overrides.
if ($Mode -eq 'Custom') {
    $promptWasBound = $PSBoundParameters.ContainsKey('Prompt')
    $promptFileWasBound = $PSBoundParameters.ContainsKey('PromptFile')
    if (-not $promptWasBound -and -not $promptFileWasBound) {
        $hasConfiguredPrompts = $configuration.ContainsKey('Prompts') -and @($configuration.Prompts).Count -gt 0
        $hasConfiguredPromptFile = $configuration.ContainsKey('PromptFile') -and
            -not [string]::IsNullOrWhiteSpace([string]$configuration.PromptFile)
        if ($hasConfiguredPrompts -and $hasConfiguredPromptFile) {
            throw 'Configure Prompts or PromptFile, not both.'
        }
        if ($hasConfiguredPrompts) {
            $Prompt = [string[]]@($configuration.Prompts)
        }
        elseif ($hasConfiguredPromptFile) {
            $configuredPromptFile = [string]$configuration.PromptFile
            $PromptFile = if ([System.IO.Path]::IsPathRooted($configuredPromptFile)) {
                $configuredPromptFile
            }
            else {
                Join-Path $configurationDirectory $configuredPromptFile
            }
        }
    }
    if (-not $PSBoundParameters.ContainsKey('Seed') -and $configuration.ContainsKey('Seeds')) {
        $Seed = [long[]]@($configuration.Seeds)
    }
}

# =============================================================================
# IMMUTABLE REPRODUCTION CONTRACT
# =============================================================================
# These versions, hashes, dimensions, and strengths are deliberately not user configuration.
# Changing one creates a different experiment and should result in a new bundle identifier.
$ComfyCommit = '62b3c94bd45154f6486c7abf1b9efcacee96ea69'
$MethodBundleId = 'ideogram4_correction_v4'
$BundledArtifactRoot = Join-Path $PSScriptRoot 'assets'
$FrozenCasesSchema = 'ideogram4_frozen_validation_cases_v2'
$FrozenCasesPath = Join-Path $PSScriptRoot 'frozen-validation.cases.psd1'
$HuggingFaceRevision = 'bbee2ab2b14b2b5223448d12d6e31e5f9cec0546'
$PythonVersion = '3.12.10'
$TorchVersion = '2.12.0+cu130'
$TorchVisionVersion = '0.27.0+cu130'
$TorchAudioVersion = '2.11.0+cu130'
$PipVersion = '25.0.1'
$ModelLicenseUrl = 'https://huggingface.co/ideogram-ai/ideogram-4-fp8/blob/main/LICENSE.md'
$RootSchema = 'ideogram4_correction_reproduction_root_v4'
$ResultSchema = 'ideogram4_correction_reproduction_results_v4'
$RuntimeReserveBytes = 12L * 1024L * 1024L * 1024L
$MinimumTestedVramMiB = 24000
$MinimumFreeVramMiB = 22000
$GenerationTimeoutSeconds = 1800

# Exact model inventory. Byte lengths catch truncation quickly; SHA-256 catches corruption
# or a silently replaced upstream file. These four files total roughly 29.5 GB.
$ModelSpecs = @(
    [pscustomobject]@{
        Kind = 'diffusion_models'
        Name = 'ideogram4_fp8_scaled.safetensors'
        Bytes = 9280741285L
        Sha256 = '49a946f1b0f8bcf5eab7d3b1ecc7b453c104e034cb1b592032745692724bd306'
    },
    [pscustomobject]@{
        Kind = 'diffusion_models'
        Name = 'ideogram4_unconditional_fp8_scaled.safetensors'
        Bytes = 9280741293L
        Sha256 = '9b359007dae162cca7591d00868feea733eb7c56e56e3a214a4d5a9a2a07cd60'
    },
    [pscustomobject]@{
        Kind = 'text_encoders'
        Name = 'qwen3vl_8b_fp8_scaled.safetensors'
        Bytes = 10588637512L
        Sha256 = '4ba424cf62e51392e4d1a39933e803706f4e823c1065f36aaf149c6453f66bcd'
    },
    [pscustomobject]@{
        Kind = 'vae'
        Name = 'flux2-vae.safetensors'
        Bytes = 336211292L
        Sha256 = '868fe7b343cc8f3a19dbcfcafbc3d5f888802be3f89bd81b65b3621a066ce8f3'
    }
)

# Local correction bundle inventory. Unlike Python, ComfyUI, and model weights, these files
# ship beside this script and never come from the research application's source repository.
$ArtifactSpecs = @(
    [pscustomobject]@{
        RelativePath = 'comfy-patches/030-core-ideogram4-block-patch.patch'
        Bytes = 4508L
        Sha256 = '0de81873a1a02c53cd309095e945b99b8f689a0356718bcdb58d79994ee3766a'
    },
    [pscustomobject]@{
        RelativePath = 'comfy-nodes/ComfyUI-Ideogram4Debanner/__init__.py'
        Bytes = 4999L
        Sha256 = 'a46c0c5ec65788293b76b6b96d9d0d8476c63207b8542a58ba37d036ee4790fe'
    },
    [pscustomobject]@{
        RelativePath = 'comfy-nodes/ComfyUI-Ideogram4Debanner/README.md'
        Bytes = 872L
        Sha256 = '2876686765fefe0b5f3a855991faaa2bccf52667ecdf9d299f85bf04ff9159df'
    },
    [pscustomobject]@{
        RelativePath = 'comfy-nodes/ComfyUI-Ideogram4Debanner/models/ideogram4_correction_v1.json'
        Bytes = 678L
        Sha256 = '3cdf853e415f93b5beaffb42e574f0d4b183b08054a9aa90e2594c2f21a301f1'
    },
    [pscustomobject]@{
        RelativePath = 'comfy-nodes/ComfyUI-Ideogram4Debanner/models/ideogram4_correction_v1.safetensors'
        Bytes = 2359784L
        Sha256 = '5ce873adae5701e9d5f05ebfa8f8b923a1622745c6e9a2bcb3e22fd090ed30c3'
    }
)

$PythonSpec = [pscustomobject]@{
    Url = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
    Bytes = 11133606L
    Sha256 = '4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3'
}
$PipWheelSpec = [pscustomobject]@{
    Url = 'https://files.pythonhosted.org/packages/c9/bc/b7db44f5f39f9d0494071bddae6880eb645970366d0a200022a1a93d57f5/pip-25.0.1-py3-none-any.whl'
    Bytes = 1841526L
    Sha256 = 'c46efd13b6aa8279f33f2864459c8ce587ea6a1a59ee20de055868d8f7688f7f'
}
$ComfyArchiveUrl = "https://codeload.github.com/comfyanonymous/ComfyUI/zip/$ComfyCommit"
$ComfyArchiveBytes = 12337135L
$ComfyArchiveSha256 = 'c53d205a6c17251c21f1503bd085f157bc06fc74ee875dfb6e42552ba6f5b9ec'
$OriginalIdeogramModelSha256 = '607edb99d0dced4b9bea702777e5ebda3e9381e1a3dc99d6f604e2a1385188b3'

# Exact clean-environment resolution from 2026-08-17. ComfyUI's own top-level
# requirements are still installed, but this constraint set prevents transitive drift.
$LockedRequirements = @'
aiohappyeyeballs==2.7.1
aiohttp==3.14.3
aiosignal==1.4.0
alembic==1.19.1
annotated-doc==0.0.5
annotated-types==0.8.0
anyio==4.14.2
attrs==26.1.0
av==18.1.0
blake3==1.0.9
certifi==2026.7.22
charset-normalizer==3.5.1
click==8.4.2
colorama==0.4.6
comfy-aimdo==0.4.13
comfy-angle==0.1.0
comfy-kitchen==0.2.30
comfyui-embedded-docs==0.5.9
comfyui-workflow-templates-core==0.3.302
comfyui-workflow-templates-json==0.1.37
comfyui-workflow-templates-media-api==0.3.84
comfyui-workflow-templates-media-assets-01==0.1.24
comfyui-workflow-templates-media-image==0.3.160
comfyui-workflow-templates-media-other==0.3.229
comfyui-workflow-templates-media-video==0.3.101
comfyui-frontend-package==1.48.7
comfyui-workflow-templates==0.11.37
einops==0.8.2
filelock==3.32.3
frozenlist==1.8.0
fsspec==2026.7.0
greenlet==3.5.5
h11==0.16.0
hf-xet==1.6.0
httpcore==1.0.9
httpx==0.28.1
huggingface-hub==1.27.0
idna==3.18
Jinja2==3.1.6
kornia==0.8.3
kornia-rs==0.1.14
Mako==1.4.1
markdown-it-py==4.2.0
MarkupSafe==3.0.3
mdurl==0.1.2
mpmath==1.3.0
multidict==6.7.1
networkx==3.6.1
numpy==2.5.2
packaging==26.3
pillow==12.3.0
propcache==0.5.2
psutil==7.2.2
pydantic==2.13.4
pydantic-settings==2.15.0
pydantic-core==2.46.4
Pygments==2.21.0
PyOpenGL==3.1.10
python-dotenv==1.2.3
PyYAML==6.0.3
regex==2026.7.19
requests==2.34.2
rich==15.0.0
safetensors==0.8.0
scipy==1.18.0
sentencepiece==0.2.2
setuptools==81.0.0
shellingham==1.5.4
simpleeval==1.0.7
spandrel==0.4.2
SQLAlchemy==2.0.52
sympy==1.14.0
tokenizers==0.22.2
torch==2.12.0+cu130
torchaudio==2.11.0+cu130
torchsde==0.2.6
torchvision==0.27.0+cu130
tqdm==4.70.0
trampoline==0.1.2
transformers==5.15.0
typer==0.27.1
typing-inspection==0.4.4
typing_extensions==4.16.0
urllib3==2.7.0
yarl==1.24.5
'@

# =============================================================================
# SMALL FILE, HASH, AND DISPLAY HELPERS
# =============================================================================

# Print a visually distinct phase boundary without hiding command output.
function Write-Stage {
    param([Parameter(Mandatory)][string] $Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

# Write stable UTF-8 without a byte-order mark. This keeps JSON, YAML, and Python portable.
function Write-Utf8File {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Text
    )
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

# Return a lowercase SHA-256 digest for comparisons and machine-readable manifests.
function Get-Sha256 {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Require a file to match both the frozen byte length and cryptographic digest.
function Assert-VerifiedFile {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][long] $Bytes,
        [Parameter(Mandatory)][string] $Sha256
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file is missing: $Path"
    }
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne $Bytes) {
        throw "Size mismatch for $Path. Expected $Bytes bytes; found $($file.Length)."
    }
    $actual = Get-Sha256 -Path $Path
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Path. Expected $Sha256; found $actual."
    }
}

# Walk upward without creating directories so disk space can be checked before downloads.
function Get-ExistingAncestor {
    param([Parameter(Mandatory)][string] $Path)
    $candidate = [System.IO.Path]::GetFullPath($Path)
    while (-not (Test-Path -LiteralPath $candidate)) {
        $parent = Split-Path -Parent $candidate
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $candidate) {
            throw "Cannot resolve an existing parent for $Path"
        }
        $candidate = $parent
    }
    return $candidate
}

# Resolve free space on the local drive that will contain the isolated environment.
function Get-FreeBytes {
    param([Parameter(Mandatory)][string] $Path)
    $ancestor = Get-ExistingAncestor -Path $Path
    $item = Get-Item -LiteralPath $ancestor
    if ($null -eq $item.PSDrive -or $null -eq $item.PSDrive.Free) {
        throw "The reproduction root must be on a local filesystem with measurable free space: $Path"
    }
    return [long]$item.PSDrive.Free
}

# Locate NVIDIA's read-only diagnostic CLI either through PATH or its standard install path.
function Find-NvidiaSmi {
    $command = Get-Command 'nvidia-smi.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }
    $standard = Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'
    if (Test-Path -LiteralPath $standard -PathType Leaf) {
        return $standard
    }
    throw 'nvidia-smi.exe was not found. Install a working NVIDIA driver before running this pipeline.'
}

# Read the chosen GPU's identity and memory without initializing CUDA or loading a model.
function Get-GpuRecord {
    param(
        [Parameter(Mandatory)][string] $NvidiaSmi,
        [Parameter(Mandatory)][int] $Index
    )
    $lines = & $NvidiaSmi '--query-gpu=index,name,driver_version,memory.total,memory.free' '--format=csv,noheader,nounits'
    if ($LASTEXITCODE -ne 0) {
        throw "nvidia-smi failed with exit code $LASTEXITCODE"
    }
    foreach ($line in @($lines)) {
        $parts = @($line -split ',' | ForEach-Object { $_.Trim() })
        if ($parts.Count -eq 5 -and [int]$parts[0] -eq $Index) {
            return [pscustomobject]@{
                Index = [int]$parts[0]
                Name = $parts[1]
                DriverVersion = $parts[2]
                TotalVramMiB = [int]$parts[3]
                FreeVramMiB = [int]$parts[4]
            }
        }
    }
    throw "GPU index $Index was not reported by nvidia-smi."
}

# Refuse to collide with any existing service. The script never takes over an occupied port.
function Test-PortFree {
    param([Parameter(Mandatory)][int] $LocalPort)
    $listener = Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue
    if ($null -ne $listener) {
        $owners = @($listener | Select-Object -ExpandProperty OwningProcess -Unique) -join ', '
        throw "Port $LocalPort is already listening (PID(s): $owners). Choose another -Port."
    }
}

# Create one redirect-aware client shared by preflight and all resumable downloads.
function New-HttpClient {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $true
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('Ideogram4CorrectionReproduction/1.0')
    return $client
}

# Send headers-only requests so every required host is checked before large downloads begin.
function Test-RemoteEndpoint {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient] $Client,
        [Parameter(Mandatory)][string] $Url,
        [hashtable] $Headers = @{}
    )
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Head, $Url)
    try {
        foreach ($entry in $Headers.GetEnumerator()) {
            if (-not $request.Headers.TryAddWithoutValidation($entry.Key, [string]$entry.Value)) {
                throw "Could not add HTTP header $($entry.Key)"
            }
        }
        $response = $Client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) {
                throw "Remote preflight failed for $Url with HTTP $([int]$response.StatusCode)."
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

# Download into a .partial file, resume with an HTTP Range request, verify, then rename.
# A completed destination is reused only after it passes the same size/hash checks.
function Invoke-VerifiedDownload {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient] $Client,
        [Parameter(Mandatory)][string] $Url,
        [Parameter(Mandatory)][string] $Destination,
        [long] $ExpectedBytes = -1,
        [string] $ExpectedSha256,
        [hashtable] $Headers = @{}
    )

    # Never overwrite a finished file. A mismatch is evidence that needs investigation.
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        if ($ExpectedBytes -ge 0 -and -not [string]::IsNullOrEmpty($ExpectedSha256)) {
            Assert-VerifiedFile -Path $Destination -Bytes $ExpectedBytes -Sha256 $ExpectedSha256
            Write-Host "Verified existing: $Destination"
            return
        }
        Write-Host "Using existing pinned archive: $Destination"
        return
    }

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $partial = "$Destination.partial"
    $offset = 0L
    if (Test-Path -LiteralPath $partial -PathType Leaf) {
        $offset = (Get-Item -LiteralPath $partial).Length
        if ($ExpectedBytes -ge 0 -and $offset -gt $ExpectedBytes) {
            throw "Partial download is larger than the expected file: $partial"
        }
    }

    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $Url)
    # A prior interrupted run leaves only .partial. Request the remaining byte range.
    if ($offset -gt 0) {
        $request.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue($offset, $null)
    }
    foreach ($entry in $Headers.GetEnumerator()) {
        if (-not $request.Headers.TryAddWithoutValidation($entry.Key, [string]$entry.Value)) {
            throw "Could not add HTTP header $($entry.Key)"
        }
    }

    $response = $null
    $networkStream = $null
    $fileStream = $null
    try {
        $response = $Client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Download failed for $Url with HTTP $([int]$response.StatusCode)."
        }
        # Servers that ignore Range return 200. Restart only the managed partial file in
        # that case; no completed artifact or user file is replaced.
        $append = $offset -gt 0 -and [int]$response.StatusCode -eq 206
        if ($offset -gt 0 -and -not $append) {
            $offset = 0L
        }
        $mode = if ($append) { [System.IO.FileMode]::Append } else { [System.IO.FileMode]::Create }
        $networkStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $fileStream = New-Object System.IO.FileStream($partial, $mode, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $buffer = New-Object byte[] (8 * 1024 * 1024)
        $written = $offset
        $nextReport = [DateTime]::UtcNow
        while (($read = $networkStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $fileStream.Write($buffer, 0, $read)
            $written += $read
            if ([DateTime]::UtcNow -ge $nextReport) {
                if ($ExpectedBytes -gt 0) {
                    $percent = [Math]::Min(100, [Math]::Round(100.0 * $written / $ExpectedBytes, 1))
                    Write-Host ("Downloading {0}: {1:N1}% ({2:N2}/{3:N2} GiB)" -f (Split-Path -Leaf $Destination), $percent, ($written / 1GB), ($ExpectedBytes / 1GB))
                }
                else {
                    Write-Host ("Downloading {0}: {1:N2} GiB" -f (Split-Path -Leaf $Destination), ($written / 1GB))
                }
                $nextReport = [DateTime]::UtcNow.AddSeconds(10)
            }
        }
        $fileStream.Flush()
    }
    finally {
        if ($null -ne $fileStream) { $fileStream.Dispose() }
        if ($null -ne $networkStream) { $networkStream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
    }

    if ($ExpectedBytes -ge 0 -and -not [string]::IsNullOrEmpty($ExpectedSha256)) {
        Assert-VerifiedFile -Path $partial -Bytes $ExpectedBytes -Sha256 $ExpectedSha256
    }
    Move-Item -LiteralPath $partial -Destination $Destination
    Write-Host "Downloaded and verified: $Destination"
}

# Run a child command synchronously and turn any nonzero exit into a hard pipeline failure.
function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $WorkingDirectory
    )
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

# =============================================================================
# EXPERIMENT INPUTS AND EXACT COMFYUI GRAPH
# =============================================================================

# Read and validate the separately published benchmark manifest. Keeping these
# records outside the executable makes the custom-prompt path easy to find and
# prevents benchmark data from being mistaken for required user configuration.
function Get-FrozenCaseManifest {
    if (-not (Test-Path -LiteralPath $FrozenCasesPath -PathType Leaf)) {
        throw "Frozen validation manifest does not exist: $FrozenCasesPath"
    }

    $manifest = Import-PowerShellDataFile -LiteralPath $FrozenCasesPath
    if ([string]$manifest.Schema -ne $FrozenCasesSchema) {
        throw "Frozen validation manifest schema must be '$FrozenCasesSchema'."
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.SmokeCaseId)) {
        throw 'Frozen validation manifest must define SmokeCaseId.'
    }

    $rows = @($manifest.Cases)
    if ($rows.Count -eq 0) {
        throw 'Frozen validation manifest contains no cases.'
    }

    $seenIds = @{}
    $cases = New-Object System.Collections.Generic.List[object]
    foreach ($row in $rows) {
        foreach ($requiredKey in @('Id', 'Prompt', 'Seed', 'HistoricalBaseline')) {
            if (-not $row.ContainsKey($requiredKey)) {
                throw "Frozen validation case is missing '$requiredKey'."
            }
        }

        $caseId = [string]$row.Id
        if ([string]::IsNullOrWhiteSpace($caseId) -or $seenIds.ContainsKey($caseId)) {
            throw "Frozen validation case ID is empty or duplicated: '$caseId'."
        }
        $seenIds[$caseId] = $true

        $casePrompt = [string]$row.Prompt
        if ([string]::IsNullOrWhiteSpace($casePrompt)) {
            throw "Frozen validation case '$caseId' has an empty prompt."
        }
        $caseSeed = [long]$row.Seed
        if ($caseSeed -lt 0) {
            throw "Frozen validation case '$caseId' has a negative seed."
        }
        $historicalBaseline = [string]$row.HistoricalBaseline
        if (@('artifact', 'clean') -notcontains $historicalBaseline) {
            throw "Frozen validation case '$caseId' has an invalid HistoricalBaseline."
        }
        $cases.Add([pscustomobject]@{
            Id = $caseId
            Prompt = $casePrompt
            Seed = $caseSeed
            HistoricalBaseline = $historicalBaseline
        })
    }

    if (-not $seenIds.ContainsKey([string]$manifest.SmokeCaseId)) {
        throw "SmokeCaseId '$($manifest.SmokeCaseId)' is not present in the frozen cases."
    }

    return [pscustomobject]@{
        SmokeCaseId = [string]$manifest.SmokeCaseId
        Cases = $cases.ToArray()
    }
}

# Expand the selected mode into immutable prompt/seed case records.
# Custom mode forms the Cartesian product: every configured prompt × every configured seed.
# Smoke and FullValidation explicitly opt into the separate frozen manifest.
function Get-ReproductionCases {
    if ($Mode -in @('Smoke', 'FullValidation')) {
        if ($Prompt -or $PromptFile -or $Seed) {
            throw '-Prompt, -PromptFile, and -Seed are only valid with -Mode Custom.'
        }

        $frozen = Get-FrozenCaseManifest
        if ($Mode -eq 'Smoke') {
            $smokeCase = @($frozen.Cases | Where-Object { $_.Id -eq $frozen.SmokeCaseId })
            if ($smokeCase.Count -ne 1) {
                throw 'Frozen validation manifest did not resolve exactly one smoke case.'
            }
            return @([pscustomobject]@{
                Id = 'smoke-01'
                Prompt = $smokeCase[0].Prompt
                Seed = $smokeCase[0].Seed
                HistoricalBaseline = $smokeCase[0].HistoricalBaseline
            })
        }
        return @($frozen.Cases)
    }

    if ($Prompt -and -not [string]::IsNullOrEmpty($PromptFile)) {
        throw 'Use either -Prompt or -PromptFile, not both.'
    }
    $prompts = @()
    if ($Prompt) {
        $prompts = @($Prompt | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    elseif (-not [string]::IsNullOrEmpty($PromptFile)) {
        if (-not (Test-Path -LiteralPath $PromptFile -PathType Leaf)) {
            throw "Prompt file does not exist: $PromptFile"
        }
        $prompts = @(Get-Content -LiteralPath $PromptFile | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and -not $_.TrimStart().StartsWith('#')
        })
    }
    if ($prompts.Count -eq 0) {
        throw '-Mode Custom requires at least one -Prompt or a nonempty -PromptFile.'
    }
    if ($null -eq $Seed -or $Seed.Count -eq 0) {
        throw '-Mode Custom requires one or more explicit -Seed values.'
    }

    $cases = New-Object System.Collections.Generic.List[object]
    $caseNumber = 1
    foreach ($text in $prompts) {
        foreach ($number in $Seed) {
            $cases.Add([pscustomobject]@{
                Id = 'custom-{0:d3}' -f $caseNumber
                Prompt = [string]$text
                Seed = [long]$number
                HistoricalBaseline = $null
            })
            $caseNumber++
        }
    }
    return $cases.ToArray()
}

# Build the exact API graph for one arm:
#   baseline  - raw conditional model
#   zero      - correction node present at zero strength (identity gate)
#   corrected - frozen direction at strength 0.6
function New-Workflow {
    param(
        [Parameter(Mandatory)][string] $Text,
        [Parameter(Mandatory)][long] $NoiseSeed,
        [Parameter(Mandatory)][ValidateSet('baseline', 'zero', 'corrected')][string] $Arm,
        [Parameter(Mandatory)][string] $FilenamePrefix
    )

    # Keep explicit numeric node IDs so saved workflow JSON can be compared byte-for-byte
    # across machines. Only prompt, seed, output prefix, and correction arm vary.
    $workflow = [ordered]@{
        '1' = [ordered]@{ class_type = 'UNETLoader'; inputs = [ordered]@{ unet_name = 'ideogram4_fp8_scaled.safetensors'; weight_dtype = 'default' } }
        '40' = [ordered]@{ class_type = 'UNETLoader'; inputs = [ordered]@{ unet_name = 'ideogram4_unconditional_fp8_scaled.safetensors'; weight_dtype = 'default' } }
        '3' = [ordered]@{ class_type = 'CLIPLoader'; inputs = [ordered]@{ clip_name = 'qwen3vl_8b_fp8_scaled.safetensors'; type = 'ideogram4'; device = 'default' } }
        '4' = [ordered]@{ class_type = 'VAELoader'; inputs = [ordered]@{ vae_name = 'flux2-vae.safetensors' } }
        '6' = [ordered]@{ class_type = 'CLIPTextEncode'; inputs = [ordered]@{ text = $Text; clip = @('3', 0) } }
        '26' = [ordered]@{ class_type = 'ConditioningZeroOut'; inputs = [ordered]@{ conditioning = @('6', 0) } }
        '2' = [ordered]@{ class_type = 'CFGOverride'; inputs = [ordered]@{ model = @('1', 0); cfg = 3.0; start_percent = 0.7; end_percent = 1.0 } }
        '22' = [ordered]@{ class_type = 'DualModelGuider'; inputs = [ordered]@{ model = @('2', 0); positive = @('6', 0); model_negative = @('40', 0); negative = @('26', 0); cfg = 7.0 } }
        '11' = [ordered]@{ class_type = 'EmptyFlux2LatentImage'; inputs = [ordered]@{ width = 992; height = 992; batch_size = 1 } }
        '17' = [ordered]@{ class_type = 'Ideogram4Scheduler'; inputs = [ordered]@{ steps = 20; width = 992; height = 992; mu = 0.5; std = 1.75 } }
        '16' = [ordered]@{ class_type = 'KSamplerSelect'; inputs = [ordered]@{ sampler_name = 'euler' } }
        '18' = [ordered]@{ class_type = 'RandomNoise'; inputs = [ordered]@{ noise_seed = $NoiseSeed } }
        '23' = [ordered]@{ class_type = 'SamplerCustomAdvanced'; inputs = [ordered]@{ noise = @('18', 0); guider = @('22', 0); sampler = @('16', 0); sigmas = @('17', 0); latent_image = @('11', 0) } }
        '8' = [ordered]@{ class_type = 'VAEDecode'; inputs = [ordered]@{ samples = @('23', 0); vae = @('4', 0) } }
        '9' = [ordered]@{ class_type = 'SaveImage'; inputs = [ordered]@{ images = @('8', 0); filename_prefix = $FilenamePrefix } }
    }
    # The correction touches only the conditional model before late guidance is applied.
    # The separately loaded unconditional model remains connected directly to the guider.
    if ($Arm -ne 'baseline') {
        $strength = if ($Arm -eq 'zero') { 0.0 } else { 0.6 }
        $workflow['49'] = [ordered]@{
            class_type = 'Ideogram4CorrectionPatch'
            inputs = [ordered]@{
                model = @('1', 0)
                enabled = $true
                strength = $strength
            }
        }
        $workflow['2'].inputs.model = @('49', 0)
    }
    return $workflow
}

# =============================================================================
# LOCAL COMFYUI API, SERVER LIFECYCLE, AND REPORTING
# =============================================================================

# Queue one graph, poll its history, and copy its single SaveImage result into the run folder.
function Invoke-ComfyGeneration {
    param(
        [Parameter(Mandatory)][string] $Server,
        [Parameter(Mandatory)][System.Collections.IDictionary] $Workflow,
        [Parameter(Mandatory)][string] $ImagePath,
        [Parameter(Mandatory)][int] $TimeoutSeconds
    )
    $payload = [ordered]@{ prompt = $Workflow; client_id = [guid]::NewGuid().ToString() } | ConvertTo-Json -Depth 30 -Compress
    $queued = Invoke-RestMethod -Uri "$Server/prompt" -Method Post -ContentType 'application/json' -Body $payload
    if ($null -ne $queued.node_errors -and @($queued.node_errors.PSObject.Properties).Count -gt 0) {
        throw "ComfyUI rejected the workflow: $($queued.node_errors | ConvertTo-Json -Depth 20)"
    }
    $promptId = [string]$queued.prompt_id
    Write-Host "Queued ComfyUI prompt $promptId"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $nextReport = [DateTime]::UtcNow
    $entry = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $history = Invoke-RestMethod -Uri "$Server/history/$promptId" -Method Get
        $property = $history.PSObject.Properties[$promptId]
        if ($null -ne $property) {
            $entry = $property.Value
            break
        }
        if ([DateTime]::UtcNow -ge $nextReport) {
            Write-Host "Waiting for $promptId ..."
            $nextReport = [DateTime]::UtcNow.AddSeconds(15)
        }
        Start-Sleep -Seconds 1
    }
    if ($null -eq $entry) {
        throw "ComfyUI prompt $promptId did not finish within $TimeoutSeconds seconds."
    }
    if ([string]$entry.status.status_str -ne 'success') {
        throw "ComfyUI execution failed: $($entry.status | ConvertTo-Json -Depth 20)"
    }
    $images = @($entry.outputs.'9'.images)
    if ($images.Count -ne 1) {
        throw "Expected one SaveImage result for $promptId; received $($images.Count)."
    }
    $descriptor = $images[0]
    $query = 'filename={0}&subfolder={1}&type={2}' -f `
        [uri]::EscapeDataString([string]$descriptor.filename), `
        [uri]::EscapeDataString([string]$descriptor.subfolder), `
        [uri]::EscapeDataString([string]$descriptor.type)
    if (Test-Path -LiteralPath $ImagePath) {
        throw "Refusing to overwrite a generated image: $ImagePath"
    }
    Invoke-WebRequest -Uri "$Server/view?$query" -OutFile $ImagePath -UseBasicParsing
    return [pscustomobject]@{ PromptId = $promptId; Descriptor = $descriptor; HistoryStatus = $entry.status }
}

# Stop only the process launched by this pipeline. If the port owner does not contain the
# expected Comfy root and port in its command line, refuse to terminate it.
function Stop-IsolatedServer {
    param(
        [System.Diagnostics.Process] $Process,
        [Parameter(Mandatory)][int] $LocalPort,
        [Parameter(Mandatory)][string] $ExpectedComfyRoot
    )
    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -ErrorAction Stop
        $Process.WaitForExit(10000) | Out-Null
    }
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        $candidate = Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)" -ErrorAction Stop
        $commandLine = [string]$candidate.CommandLine
        if (-not $commandLine.Contains($ExpectedComfyRoot) -or -not $commandLine.Contains("--port $LocalPort")) {
            throw "Port $LocalPort remains owned by an unexpected process (PID $($listener.OwningProcess)); it was not stopped."
        }
        Stop-Process -Id $listener.OwningProcess -ErrorAction Stop
    }
    Start-Sleep -Seconds 1
    if (Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue) {
        throw "Safety shutdown failed: isolated port $LocalPort is still open."
    }
    Write-Host "Isolated ComfyUI port $LocalPort is stopped."
}

# Produce a dependency-free local page for human baseline/zero/corrected inspection.
function New-ComparisonHtml {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][object[]] $Cases,
        [Parameter(Mandatory)][object[]] $Results,
        [Parameter(Mandatory)][bool] $NoOpPassed
    )
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('<!doctype html><html><head><meta charset="utf-8"><title>Ideogram 4 correction reproduction</title>')
    [void]$builder.AppendLine('<style>body{font:16px system-ui;margin:24px;background:#15171a;color:#eee}h1{font-size:24px}.case{margin:24px 0;padding:18px;background:#22262b;border-radius:10px}.images{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}.arm{background:#111;padding:10px;border-radius:8px}.arm img{width:100%;height:auto}.meta{color:#b9c0c9}.pass{color:#7ee787}.missing{color:#999}@media(max-width:900px){.images{grid-template-columns:1fr}}</style></head><body>')
    [void]$builder.AppendLine('<h1>Ideogram 4 correction reproduction</h1>')
    $gateClass = if ($NoOpPassed) { 'pass' } else { '' }
    [void]$builder.AppendLine("<p class='$gateClass'>Strict zero-strength no-op: $NoOpPassed</p>")
    foreach ($case in $Cases) {
        $safePrompt = [System.Net.WebUtility]::HtmlEncode([string]$case.Prompt)
        [void]$builder.AppendLine("<section class='case'><h2>$([System.Net.WebUtility]::HtmlEncode([string]$case.Id))</h2><p>$safePrompt</p><p class='meta'>Seed: $($case.Seed)</p><div class='images'>")
        foreach ($arm in @('baseline', 'zero', 'corrected')) {
            $match = @($Results | Where-Object { $_.CaseId -eq $case.Id -and $_.Arm -eq $arm })
            [void]$builder.AppendLine("<div class='arm'><h3>$arm</h3>")
            if ($match.Count -eq 1) {
                $relative = [string]$match[0].RelativeImagePath -replace '\\', '/'
                [void]$builder.AppendLine("<img loading='lazy' src='$relative' alt='$arm output'>")
                [void]$builder.AppendLine("<p class='meta'>Pixel SHA-256: $($match[0].PixelSha256)</p>")
            }
            else {
                [void]$builder.AppendLine("<p class='missing'>The zero arm is run once as the identity gate.</p>")
            }
            [void]$builder.AppendLine('</div>')
        }
        [void]$builder.AppendLine('</div></section>')
    }
    [void]$builder.AppendLine('</body></html>')
    Write-Utf8File -Path $Path -Text $builder.ToString()
}

# =============================================================================
# PHASE 1: READ-ONLY PREFLIGHT
# =============================================================================
# Everything through -PreflightOnly is non-mutating: parse inputs, inspect GPU/port/disk,
# hash an optional model cache, verify the bundled method, and issue HTTP HEAD requests.
if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
    throw 'This bootstrap currently supports 64-bit Windows only.'
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'A 64-bit Windows installation is required.'
}

$RootFull = [System.IO.Path]::GetFullPath($Root)
$driveRoot = [System.IO.Path]::GetPathRoot($RootFull)
if ($RootFull.TrimEnd('\') -eq $driveRoot.TrimEnd('\')) {
    throw 'The reproduction root cannot be a drive root.'
}
$cases = @(Get-ReproductionCases)
$nvidiaSmi = Find-NvidiaSmi
$gpu = Get-GpuRecord -NvidiaSmi $nvidiaSmi -Index $GpuIndex

Write-Stage 'Preflight'
Write-Host "Root: $RootFull"
Write-Host "GPU: $($gpu.Name), driver $($gpu.DriverVersion), $($gpu.TotalVramMiB) MiB total, $($gpu.FreeVramMiB) MiB free"
Write-Host "Cases: $($cases.Count); planned generations: $($cases.Count * 2 + 1)"
Test-PortFree -LocalPort $Port

foreach ($artifact in $ArtifactSpecs) {
    $bundledPath = Join-Path $BundledArtifactRoot ($artifact.RelativePath -replace '/', '\')
    Assert-VerifiedFile -Path $bundledPath -Bytes $artifact.Bytes -Sha256 $artifact.Sha256
}
Write-Host "Standalone correction bundle verified: $MethodBundleId"

if (-not $AllowUntestedVram) {
    if ($gpu.TotalVramMiB -lt $MinimumTestedVramMiB) {
        throw "The exact workflow was validated with 24 GB VRAM. This GPU reports $($gpu.TotalVramMiB) MiB. Re-run with -AllowUntestedVram to attempt ComfyUI offloading without a validation claim."
    }
    if ($gpu.FreeVramMiB -lt $MinimumFreeVramMiB) {
        throw "The selected GPU has only $($gpu.FreeVramMiB) MiB free. Stop other GPU jobs or use -AllowUntestedVram."
    }
}

$modelRoot = $null
if (-not [string]::IsNullOrEmpty($ExistingModelRoot)) {
    $modelRoot = [System.IO.Path]::GetFullPath($ExistingModelRoot)
    if (-not (Test-Path -LiteralPath $modelRoot -PathType Container)) {
        throw "Existing model root does not exist: $modelRoot"
    }
}

# Calculate only the bytes still missing. Existing files count as reusable only after hash
# verification; partial model downloads reduce the additional disk requirement.
$missingModelBytes = 0L
$verifiedModelPaths = @{}
foreach ($spec in $ModelSpecs) {
    if ($null -ne $modelRoot) {
        $candidate = Join-Path (Join-Path $modelRoot $spec.Kind) $spec.Name
        Assert-VerifiedFile -Path $candidate -Bytes $spec.Bytes -Sha256 $spec.Sha256
        $verifiedModelPaths[[System.IO.Path]::GetFullPath($candidate)] = $true
    }
    else {
        $candidate = Join-Path (Join-Path (Join-Path $RootFull 'source') "ComfyUI-$ComfyCommit") (Join-Path 'models' (Join-Path $spec.Kind $spec.Name))
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            Assert-VerifiedFile -Path $candidate -Bytes $spec.Bytes -Sha256 $spec.Sha256
            $verifiedModelPaths[[System.IO.Path]::GetFullPath($candidate)] = $true
        }
        else {
            $partial = "$candidate.partial"
            $partialBytes = if (Test-Path -LiteralPath $partial -PathType Leaf) { (Get-Item -LiteralPath $partial).Length } else { 0L }
            $missingModelBytes += [Math]::Max(0L, $spec.Bytes - $partialBytes)
        }
    }
}
$requiredFreeBytes = $missingModelBytes + $RuntimeReserveBytes
$freeBytes = Get-FreeBytes -Path $RootFull
Write-Host ('Disk: {0:N2} GiB free; {1:N2} GiB required for missing models plus isolated runtime reserve' -f ($freeBytes / 1GB), ($requiredFreeBytes / 1GB))
if ($freeBytes -lt $requiredFreeBytes) {
    throw 'Insufficient free disk space for the verified model files and isolated runtime.'
}

$http = New-HttpClient
try {
    $authHeaders = @{}
    if (-not [string]::IsNullOrEmpty($HuggingFaceToken)) {
        $authHeaders['Authorization'] = "Bearer $HuggingFaceToken"
    }
    $preflightUrls = @(
        $PythonSpec.Url,
        $PipWheelSpec.Url,
        $ComfyArchiveUrl,
        'https://download.pytorch.org/whl/cu130/',
        'https://pypi.org/simple/pip/'
    )
    foreach ($url in $preflightUrls) {
        Test-RemoteEndpoint -Client $http -Url $url
    }
    if ($null -eq $modelRoot) {
        $firstModel = $ModelSpecs[0]
        $firstModelUrl = "https://huggingface.co/Comfy-Org/Ideogram-4/resolve/$HuggingFaceRevision/$($firstModel.Kind)/$($firstModel.Name)?download=true"
        Test-RemoteEndpoint -Client $http -Url $firstModelUrl -Headers $authHeaders
    }
    Write-Host 'Network endpoints are reachable.'

    # This is the only successful exit before any directory or download is created.
    if ($PreflightOnly) {
        Write-Host "Preflight passed. Full setup requires -AcceptModelLicense ($ModelLicenseUrl)."
        return
    }
    if (-not $AcceptModelLicense) {
        throw "Read $ModelLicenseUrl, then re-run with -AcceptModelLicense if you accept it."
    }

    # =============================================================================
    # PHASE 2: CLAIM AN ISOLATED, MARKED WORKING DIRECTORY
    # =============================================================================
    # A nonempty directory must carry our schema marker. This prevents the bootstrap from
    # treating an unrelated folder as disposable or resumable state.
    if (Test-Path -LiteralPath $RootFull -PathType Container) {
        $markerPath = Join-Path $RootFull '.ideogram4-reproduction-root.json'
        $children = @(Get-ChildItem -LiteralPath $RootFull -Force)
        if ($children.Count -gt 0 -and -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "Refusing to use a nonempty, unmarked directory: $RootFull"
        }
        if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
            $marker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
            if ([string]$marker.schema -ne $RootSchema) {
                throw "Unknown reproduction marker schema in $markerPath"
            }
        }
    }
    else {
        New-Item -ItemType Directory -Path $RootFull | Out-Null
    }
    $markerPath = Join-Path $RootFull '.ideogram4-reproduction-root.json'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        $markerText = [ordered]@{
            schema = $RootSchema
            created_utc = [DateTime]::UtcNow.ToString('o')
            comfy_commit = $ComfyCommit
            method_bundle = $MethodBundleId
            huggingface_revision = $HuggingFaceRevision
        } | ConvertTo-Json -Depth 5
        Write-Utf8File -Path $markerPath -Text $markerText
    }

    $downloadsDir = Join-Path $RootFull 'downloads'
    $runtimeDir = Join-Path $RootFull 'runtime'
    $sourceDir = Join-Path $RootFull 'source'
    $artifactDir = Join-Path $RootFull 'artifacts'
    New-Item -ItemType Directory -Force -Path $downloadsDir, $runtimeDir, $sourceDir, $artifactDir | Out-Null

    # =============================================================================
    # PHASE 3: INSTALL PINNED PYTHON, PYTORCH, AND COMFYUI
    # =============================================================================
    Write-Stage 'Pinned Python and ComfyUI runtime'
    $pythonZip = Join-Path $downloadsDir "python-$PythonVersion-embed-amd64.zip"
    $pipWheel = Join-Path $downloadsDir "pip-$PipVersion-py3-none-any.whl"
    $comfyZip = Join-Path $downloadsDir "ComfyUI-$ComfyCommit.zip"
    Invoke-VerifiedDownload -Client $http -Url $PythonSpec.Url -Destination $pythonZip -ExpectedBytes $PythonSpec.Bytes -ExpectedSha256 $PythonSpec.Sha256
    Invoke-VerifiedDownload -Client $http -Url $PipWheelSpec.Url -Destination $pipWheel -ExpectedBytes $PipWheelSpec.Bytes -ExpectedSha256 $PipWheelSpec.Sha256
    Invoke-VerifiedDownload -Client $http -Url $ComfyArchiveUrl -Destination $comfyZip -ExpectedBytes $ComfyArchiveBytes -ExpectedSha256 $ComfyArchiveSha256

    $pythonDir = Join-Path $runtimeDir "python-$PythonVersion"
    $pythonExe = Join-Path $pythonDir 'python.exe'
    # Python's embeddable ZIP is isolated by default. Enable site-packages explicitly, then
    # later add only this pinned Comfy source directory to its search path.
    if (-not (Test-Path -LiteralPath $pythonExe -PathType Leaf)) {
        if (Test-Path -LiteralPath $pythonDir) {
            throw "Incomplete Python extraction exists at $pythonDir. Use a new -Root so no files are overwritten."
        }
        New-Item -ItemType Directory -Path $pythonDir | Out-Null
        Expand-Archive -LiteralPath $pythonZip -DestinationPath $pythonDir
    }
    $pthPath = Join-Path $pythonDir 'python312._pth'
    $pthLines = @(Get-Content -LiteralPath $pthPath)
    if ($pthLines -contains '#import site') {
        $updatedPth = New-Object System.Collections.Generic.List[string]
        foreach ($pthLine in $pthLines) {
            if ($pthLine -eq '#import site') {
                if (-not $updatedPth.Contains('Lib\site-packages')) {
                    $updatedPth.Add('Lib\site-packages')
                }
                $updatedPth.Add('import site')
            }
            else {
                $updatedPth.Add($pthLine)
            }
        }
        Write-Utf8File -Path $pthPath -Text (($updatedPth.ToArray() -join "`n") + "`n")
        $pthLines = @($updatedPth.ToArray())
    }
    if ($pthLines -notcontains 'Lib\site-packages' -or $pthLines -notcontains 'import site') {
        throw "Unexpected embedded Python path file: $pthPath"
    }
    # Bootstrap pip from a pinned wheel using only Python's standard zipfile module. This
    # avoids the mutable get-pip.py bootstrap URL.
    if (-not (Test-Path -LiteralPath (Join-Path $pythonDir 'Lib\site-packages\pip') -PathType Container)) {
        $pipBootstrapCode = "import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])"
        Invoke-CheckedProcess -FilePath $pythonExe -Arguments @(
            '-c', $pipBootstrapCode, $pipWheel, (Join-Path $pythonDir 'Lib\site-packages')
        ) -WorkingDirectory $pythonDir
    }

    $comfyRoot = Join-Path $sourceDir "ComfyUI-$ComfyCommit"
    if (-not (Test-Path -LiteralPath (Join-Path $comfyRoot 'main.py') -PathType Leaf)) {
        if (Test-Path -LiteralPath $comfyRoot) {
            throw "Incomplete ComfyUI extraction exists at $comfyRoot. Use a new -Root so no files are overwritten."
        }
        Expand-Archive -LiteralPath $comfyZip -DestinationPath $sourceDir
    }
    $pthLines = @(Get-Content -LiteralPath $pthPath)
    if ($pthLines -notcontains $comfyRoot) {
        $pthLines += $comfyRoot
        Write-Utf8File -Path $pthPath -Text (($pthLines -join "`n") + "`n")
    }

    $requirementsLockPath = Join-Path $runtimeDir 'requirements.lock.txt'
    Write-Utf8File -Path $requirementsLockPath -Text (($LockedRequirements.Trim() + "`n"))
    $requirementsLockSha256 = Get-Sha256 -Path $requirementsLockPath
    $runtimeMarker = Join-Path $runtimeDir 'runtime-ready.json'
    $runtimeReady = $false
    if (Test-Path -LiteralPath $runtimeMarker -PathType Leaf) {
        $runtimeState = Get-Content -Raw -LiteralPath $runtimeMarker | ConvertFrom-Json
        $runtimeReady = (
            [string]$runtimeState.schema -eq 'ideogram4_correction_runtime_v2' -and
            [string]$runtimeState.requirements_lock_sha256 -eq $requirementsLockSha256
        )
    }
    # The lock hash is stored in the runtime marker. A changed lock causes a constrained
    # reconciliation instead of silently trusting an older environment.
    if (-not $runtimeReady) {
        Invoke-CheckedProcess -FilePath $pythonExe -Arguments @(
            '-m', 'pip', 'install', '--disable-pip-version-check', '--no-warn-script-location', '--no-cache-dir',
            "torch==$TorchVersion", "torchvision==$TorchVisionVersion", "torchaudio==$TorchAudioVersion",
            '--constraint', $requirementsLockPath,
            '--index-url', 'https://download.pytorch.org/whl/cu130', '--extra-index-url', 'https://pypi.org/simple'
        ) -WorkingDirectory $comfyRoot
        Invoke-CheckedProcess -FilePath $pythonExe -Arguments @(
            '-m', 'pip', 'install', '--disable-pip-version-check', '--no-warn-script-location', '--no-cache-dir',
            '--constraint', $requirementsLockPath, '--extra-index-url', 'https://download.pytorch.org/whl/cu130',
            '-r', (Join-Path $comfyRoot 'requirements.txt')
        ) -WorkingDirectory $comfyRoot
        Invoke-CheckedProcess -FilePath $pythonExe -Arguments @('-m', 'pip', 'check') -WorkingDirectory $comfyRoot
        $runtimeText = [ordered]@{
            schema = 'ideogram4_correction_runtime_v2'
            python = $PythonVersion
            torch = $TorchVersion
            torchvision = $TorchVisionVersion
            torchaudio = $TorchAudioVersion
            comfy_commit = $ComfyCommit
            requirements_lock_sha256 = $requirementsLockSha256
        } | ConvertTo-Json -Depth 5
        Write-Utf8File -Path $runtimeMarker -Text $runtimeText
    }

    # =============================================================================
    # PHASE 4: CUDA FUNCTIONAL GATE BEFORE MODEL DOWNLOADS
    # =============================================================================
    # A tiny matrix multiplication confirms that the pinned wheel can actually use the
    # selected driver. It happens before downloading 29.5 GB of model weights.
    Write-Stage 'CUDA functional gate'
    $cudaCode = "import json, torch; assert torch.cuda.is_available(), 'torch.cuda.is_available() is false'; x=torch.ones((1024,1024),device='cuda'); y=x@x; torch.cuda.synchronize(); print(json.dumps({'torch':torch.__version__,'cuda_runtime':torch.version.cuda,'device':torch.cuda.get_device_name(0),'value':float(y[0,0])}))"
    $cudaJson = & $pythonExe '-c' $cudaCode
    if ($LASTEXITCODE -ne 0) { throw 'The isolated PyTorch CUDA functional test failed.' }
    $cuda = $cudaJson | ConvertFrom-Json
    Write-Host "PyTorch $($cuda.torch), CUDA runtime $($cuda.cuda_runtime), device $($cuda.device)"

    # =============================================================================
    # PHASE 5: INSTALL THE REVERSIBLE HOOK AND LOCAL CORRECTION BUNDLE
    # =============================================================================
    Write-Stage 'Reversible core hook and correction node'
    foreach ($artifact in $ArtifactSpecs) {
        $artifactPath = Join-Path $artifactDir ($artifact.RelativePath -replace '/', '\')
        $bundledPath = Join-Path $BundledArtifactRoot ($artifact.RelativePath -replace '/', '\')
        Assert-VerifiedFile -Path $bundledPath -Bytes $artifact.Bytes -Sha256 $artifact.Sha256
        if (Test-Path -LiteralPath $artifactPath -PathType Leaf) {
            Assert-VerifiedFile -Path $artifactPath -Bytes $artifact.Bytes -Sha256 $artifact.Sha256
            Write-Host "Verified existing: $artifactPath"
        }
        else {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $artifactPath) | Out-Null
            Copy-Item -LiteralPath $bundledPath -Destination $artifactPath
            Assert-VerifiedFile -Path $artifactPath -Bytes $artifact.Bytes -Sha256 $artifact.Sha256
            Write-Host "Installed bundled artifact: $artifactPath"
        }
    }

    $helperPath = Join-Path $runtimeDir 'reproduction_helper.py'
    # This standard-library helper keeps unified-diff application and pixel hashing exact.
    # It is generated inside the marked environment so the distributed bundle stays simple.
    $helperCode = @'
import hashlib
import json
import math
import os
import re
import sys
from pathlib import Path


def apply_patch(patch_path: Path, root: Path) -> None:
    lines = patch_path.read_text(encoding="utf-8").splitlines(keepends=True)
    index = 0
    applied = 0
    while index < len(lines):
        if not lines[index].startswith("diff --git "):
            index += 1
            continue
        index += 1
        while index < len(lines) and not lines[index].startswith("--- "):
            index += 1
        if index + 1 >= len(lines) or not lines[index + 1].startswith("+++ "):
            raise RuntimeError("Malformed unified diff file header")
        new_name = lines[index + 1][4:].strip()
        if not new_name.startswith("b/"):
            raise RuntimeError(f"Unsafe patch target: {new_name}")
        relative = Path(new_name[2:])
        target = (root / relative).resolve()
        root_resolved = root.resolve()
        if root_resolved not in target.parents:
            raise RuntimeError(f"Patch target escapes root: {target}")
        source = target.read_text(encoding="utf-8").splitlines(keepends=True)
        index += 2
        delta = 0
        while index < len(lines) and not lines[index].startswith("diff --git "):
            if not lines[index].startswith("@@ "):
                index += 1
                continue
            match = re.match(r"@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", lines[index])
            if match is None:
                raise RuntimeError(f"Malformed hunk header: {lines[index].rstrip()}")
            old_start = int(match.group(1)) - 1 + delta
            index += 1
            old_lines = []
            new_lines = []
            while index < len(lines) and not lines[index].startswith("@@ ") and not lines[index].startswith("diff --git "):
                marker = lines[index][:1]
                if marker == " ":
                    old_lines.append(lines[index][1:])
                    new_lines.append(lines[index][1:])
                elif marker == "-":
                    old_lines.append(lines[index][1:])
                elif marker == "+":
                    new_lines.append(lines[index][1:])
                elif lines[index].startswith("\\ No newline at end of file"):
                    pass
                else:
                    break
                index += 1
            if source[old_start:old_start + len(old_lines)] != old_lines:
                raise RuntimeError(f"Patch context mismatch at {relative}:{old_start + 1}")
            source[old_start:old_start + len(old_lines)] = new_lines
            delta += len(new_lines) - len(old_lines)
        temporary = target.with_name(target.name + ".ideogram4-patch.tmp")
        if temporary.exists():
            raise RuntimeError(f"Refusing to replace existing temporary file: {temporary}")
        temporary.write_text("".join(source), encoding="utf-8", newline="")
        os.replace(temporary, target)
        applied += 1
    if applied == 0:
        raise RuntimeError("The patch did not contain a file diff")


def pixel_hash(path: Path) -> str:
    from PIL import Image
    with Image.open(path) as image:
        rgb = image.convert("RGB")
        payload = rgb.width.to_bytes(8, "little") + rgb.height.to_bytes(8, "little") + rgb.tobytes()
    return hashlib.sha256(payload).hexdigest()


def sigma_sequence(steps: int, width: int, height: int, mu: float, std: float):
    import torch
    logsnr_max = 18.0
    logsnr_min = -15.0
    mean = mu + 0.5 * math.log((width * height) / (512 * 512))
    u = torch.linspace(0.0, 1.0, steps + 1, dtype=torch.float64)
    t = 1.0 - torch.special.expit(mean + std * torch.special.ndtri(u))
    t_min = 1.0 / (1.0 + math.exp(0.5 * logsnr_max))
    t_max = 1.0 / (1.0 + math.exp(0.5 * logsnr_min))
    sigmas = (1.0 - t.clamp(t_min, t_max)).flip(0).to(torch.float32)
    sigmas[-1] = 0.0
    return [float(value) for value in sigmas]


if __name__ == "__main__":
    command = sys.argv[1]
    if command == "apply-patch":
        apply_patch(Path(sys.argv[2]), Path(sys.argv[3]))
    elif command == "pixel-hash":
        print(pixel_hash(Path(sys.argv[2])))
    elif command == "sigmas":
        print(json.dumps(sigma_sequence(int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), float(sys.argv[5]), float(sys.argv[6]))))
    else:
        raise SystemExit(f"Unknown helper command: {command}")
'@
    Write-Utf8File -Path $helperPath -Text $helperCode

    $coreModelPath = Join-Path $comfyRoot 'comfy\ldm\ideogram4\model.py'
    $patchPath = Join-Path $artifactDir 'comfy-patches\030-core-ideogram4-block-patch.patch'
    $patchMarker = Join-Path $artifactDir 'core-patch-state.json'
    # Patch only a byte-verified upstream file. Preserve its original beside it, and record
    # both hashes so a later run detects any unexpected change.
    if (Test-Path -LiteralPath $patchMarker -PathType Leaf) {
        $patchState = Get-Content -Raw -LiteralPath $patchMarker | ConvertFrom-Json
        $currentPatchedHash = Get-Sha256 -Path $coreModelPath
        if ($currentPatchedHash -ne [string]$patchState.patched_sha256) {
            throw 'The patched ComfyUI core file changed after patch installation.'
        }
    }
    else {
        $originalHash = Get-Sha256 -Path $coreModelPath
        if ($originalHash -ne $OriginalIdeogramModelSha256) {
            throw "Pinned ComfyUI source mismatch before patching. Expected $OriginalIdeogramModelSha256; found $originalHash."
        }
        $backupPath = "$coreModelPath.ideogram4-original"
        if (Test-Path -LiteralPath $backupPath) {
            Assert-VerifiedFile -Path $backupPath -Bytes (Get-Item -LiteralPath $coreModelPath).Length -Sha256 $OriginalIdeogramModelSha256
        }
        else {
            Copy-Item -LiteralPath $coreModelPath -Destination $backupPath
        }
        Invoke-CheckedProcess -FilePath $pythonExe -Arguments @($helperPath, 'apply-patch', $patchPath, $comfyRoot) -WorkingDirectory $comfyRoot
        $patchedHash = Get-Sha256 -Path $coreModelPath
        if ($patchedHash -eq $OriginalIdeogramModelSha256) {
            throw 'Core patch application made no change.'
        }
        $patchText = [ordered]@{
            schema = 'ideogram4_core_patch_state_v1'
            patch_sha256 = Get-Sha256 -Path $patchPath
            original_sha256 = $OriginalIdeogramModelSha256
            patched_sha256 = $patchedHash
        } | ConvertTo-Json -Depth 5
        Write-Utf8File -Path $patchMarker -Text $patchText
    }

    $nodeSource = Join-Path $artifactDir 'comfy-nodes\ComfyUI-Ideogram4Debanner'
    $nodeDestination = Join-Path $comfyRoot 'custom_nodes\ComfyUI-Ideogram4Debanner'
    foreach ($artifact in @($ArtifactSpecs | Where-Object { $_.RelativePath.StartsWith('comfy-nodes/') })) {
        $nodeRelative = $artifact.RelativePath.Substring('comfy-nodes/ComfyUI-Ideogram4Debanner/'.Length) -replace '/', '\'
        $sourcePath = Join-Path $nodeSource $nodeRelative
        $destinationPath = Join-Path $nodeDestination $nodeRelative
        if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
            Assert-VerifiedFile -Path $destinationPath -Bytes $artifact.Bytes -Sha256 $artifact.Sha256
        }
        else {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPath) | Out-Null
            Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
            Assert-VerifiedFile -Path $destinationPath -Bytes $artifact.Bytes -Sha256 $artifact.Sha256
        }
    }

    # =============================================================================
    # PHASE 6: DOWNLOAD OR READ-ONLY REFERENCE THE FOUR MODEL FILES
    # =============================================================================
    Write-Stage 'Model verification'
    if ($null -eq $modelRoot) {
        $modelRoot = Join-Path $comfyRoot 'models'
        foreach ($spec in $ModelSpecs) {
            $destination = Join-Path (Join-Path $modelRoot $spec.Kind) $spec.Name
            $url = "https://huggingface.co/Comfy-Org/Ideogram-4/resolve/$HuggingFaceRevision/$($spec.Kind)/$($spec.Name)?download=true"
            $fullDestination = [System.IO.Path]::GetFullPath($destination)
            if ($verifiedModelPaths.ContainsKey($fullDestination)) {
                Write-Host "Verified during preflight: $destination"
            }
            else {
                Invoke-VerifiedDownload -Client $http -Url $url -Destination $destination -ExpectedBytes $spec.Bytes -ExpectedSha256 $spec.Sha256 -Headers $authHeaders
            }
        }
    }
    else {
        foreach ($spec in $ModelSpecs) {
            $candidate = Join-Path (Join-Path $modelRoot $spec.Kind) $spec.Name
            $fullCandidate = [System.IO.Path]::GetFullPath($candidate)
            if ($verifiedModelPaths.ContainsKey($fullCandidate)) {
                Write-Host "Verified during preflight: $candidate"
            }
            else {
                Assert-VerifiedFile -Path $candidate -Bytes $spec.Bytes -Sha256 $spec.Sha256
                Write-Host "Verified cached model: $candidate"
            }
        }
    }

    $extraModelConfig = $null
    $localComfyModels = [System.IO.Path]::GetFullPath((Join-Path $comfyRoot 'models'))
    if ([System.IO.Path]::GetFullPath($modelRoot) -ne $localComfyModels) {
        $extraModelConfig = Join-Path $artifactDir 'extra_model_paths.yaml'
        $yamlBase = $modelRoot -replace '\\', '/'
        $yaml = "reproduction_models:`n  base_path: '$yamlBase'`n  diffusion_models: diffusion_models`n  text_encoders: text_encoders`n  vae: vae`n"
        Write-Utf8File -Path $extraModelConfig -Text $yaml
    }

    $pipFreezePath = Join-Path $artifactDir 'pip-freeze.txt'
    $pipFreeze = & $pythonExe '-m' 'pip' 'freeze'
    if ($LASTEXITCODE -ne 0) { throw 'pip freeze failed.' }
    Write-Utf8File -Path $pipFreezePath -Text (($pipFreeze -join "`n") + "`n")

    # SetupOnly is a deliberate stopping point: everything is installed and verified, but
    # no Comfy server is started and no generation is queued.
    if ($SetupOnly) {
        Write-Host 'Setup and verification completed. No generation server was started.'
        return
    }

    # =============================================================================
    # PHASE 7: START LOCALHOST COMFYUI AND RUN PAIRED CASES
    # =============================================================================
    Write-Stage 'Isolated paired generation'
    Test-PortFree -LocalPort $Port
    $runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $runDir = Join-Path (Join-Path $RootFull 'runs') $runId
    $imagesDir = Join-Path $runDir 'images'
    $workflowsDir = Join-Path $runDir 'workflows'
    $comfyOutputDir = Join-Path $runDir 'comfy-output'
    $comfyInputDir = Join-Path $runDir 'comfy-input'
    $comfyTempDir = Join-Path $runDir 'comfy-temp'
    $comfyUserDir = Join-Path $runDir 'comfy-user'
    New-Item -ItemType Directory -Path $runDir, $imagesDir, $workflowsDir, $comfyOutputDir, $comfyInputDir, $comfyTempDir, $comfyUserDir | Out-Null
    $stdoutPath = Join-Path $runDir 'comfy.stdout.log'
    $stderrPath = Join-Path $runDir 'comfy.stderr.log'

    $serverProcess = $null
    $previousCudaVisibleDevices = $env:CUDA_VISIBLE_DEVICES
    try {
        # CUDA_VISIBLE_DEVICES isolates the requested physical GPU. Inside this child process
        # it becomes cuda:0; the parent PowerShell value is restored in the outer finally.
        $env:CUDA_VISIBLE_DEVICES = [string]$GpuIndex
        $serverArguments = @(
            ('"{0}"' -f (Join-Path $comfyRoot 'main.py')),
            '--listen', '127.0.0.1', '--port', [string]$Port,
            '--output-directory', ('"{0}"' -f $comfyOutputDir),
            '--input-directory', ('"{0}"' -f $comfyInputDir),
            '--temp-directory', ('"{0}"' -f $comfyTempDir),
            '--user-directory', ('"{0}"' -f $comfyUserDir),
            '--disable-auto-launch'
        )
        if ($null -ne $extraModelConfig) {
            $serverArguments += @('--extra-model-paths-config', ('"{0}"' -f $extraModelConfig))
        }
        $serverProcess = Start-Process -FilePath $pythonExe -ArgumentList $serverArguments -WorkingDirectory $comfyRoot -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
        $startDeadline = [DateTime]::UtcNow.AddMinutes(3)
        $listener = $null
        while ([DateTime]::UtcNow -lt $startDeadline) {
            Start-Sleep -Milliseconds 500
            $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
            if ($null -ne $listener) { break }
            if ($serverProcess.HasExited) {
                throw "Isolated ComfyUI exited during startup. Read $stderrPath"
            }
        }
        if ($null -eq $listener) {
            throw "Isolated ComfyUI did not start within three minutes. Read $stderrPath"
        }

        $server = "http://127.0.0.1:$Port"
        # Node discovery is an executable compatibility gate. A present file is insufficient
        # if the core capability marker or imports are wrong.
        $requiredNodes = @('Ideogram4CorrectionPatch', 'DualModelGuider', 'CFGOverride', 'Ideogram4Scheduler', 'EmptyFlux2LatentImage')
        $objectInfo = Invoke-RestMethod -Uri "$server/object_info" -Method Get
        foreach ($nodeName in $requiredNodes) {
            if ($null -eq $objectInfo.PSObject.Properties[$nodeName]) {
                throw "Required ComfyUI node was not discovered: $nodeName"
            }
        }
        $systemStats = Invoke-RestMethod -Uri "$server/system_stats" -Method Get
        $sigmaJson = & $pythonExe $helperPath 'sigmas' '20' '992' '992' '0.5' '1.75'
        if ($LASTEXITCODE -ne 0) { throw 'Sigma sequence calculation failed.' }
        $sigmas = @($sigmaJson | ConvertFrom-Json)

        $resultRows = New-Object System.Collections.Generic.List[object]
        $generationNumber = 0
        $generationTotal = $cases.Count * 2 + 1
        # The first case has three arms so the identity gate is proved on this exact runtime.
        # Remaining cases need only baseline and corrected arms after that global gate passes.
        for ($caseIndex = 0; $caseIndex -lt $cases.Count; $caseIndex++) {
            $case = $cases[$caseIndex]
            $arms = if ($caseIndex -eq 0) { @('baseline', 'zero', 'corrected') } else { @('baseline', 'corrected') }
            foreach ($arm in $arms) {
                $generationNumber++
                Write-Host "[$generationNumber/$generationTotal] $($case.Id) / $arm"
                $filenamePrefix = "reproduction/$($case.Id)-$arm"
                $workflow = New-Workflow -Text $case.Prompt -NoiseSeed $case.Seed -Arm $arm -FilenamePrefix $filenamePrefix
                $workflowPath = Join-Path $workflowsDir "$($case.Id)-$arm.json"
                Write-Utf8File -Path $workflowPath -Text ($workflow | ConvertTo-Json -Depth 30)
                $imagePath = Join-Path $imagesDir "$($case.Id)-$arm.png"
                $execution = Invoke-ComfyGeneration -Server $server -Workflow $workflow -ImagePath $imagePath -TimeoutSeconds $GenerationTimeoutSeconds
                $pixelHash = (& $pythonExe $helperPath 'pixel-hash' $imagePath).Trim()
                if ($LASTEXITCODE -ne 0) { throw "Pixel hashing failed for $imagePath" }
                $resultRow = [pscustomobject]@{
                    CaseId = [string]$case.Id
                    Arm = $arm
                    Prompt = [string]$case.Prompt
                    Seed = [long]$case.Seed
                    PromptId = [string]$execution.PromptId
                    RelativeImagePath = "images/$($case.Id)-$arm.png"
                    RelativeWorkflowPath = "workflows/$($case.Id)-$arm.json"
                    FileSha256 = Get-Sha256 -Path $imagePath
                    PixelSha256 = $pixelHash
                    HistoricalBaseline = $case.HistoricalBaseline
                }
                $resultRows.Add($resultRow)
                # Abort before any active correction if zero strength changes even one decoded
                # pixel. This is stricter than comparing PNG files, whose metadata can differ.
                if ($caseIndex -eq 0 -and $arm -eq 'zero') {
                    $earlyBaseline = @($resultRows | Where-Object { $_.CaseId -eq $case.Id -and $_.Arm -eq 'baseline' })[0]
                    if ($earlyBaseline.PixelSha256 -ne $resultRow.PixelSha256) {
                        $failurePath = Join-Path $runDir 'strict-noop-failure.json'
                        $failure = [ordered]@{
                            schema = 'ideogram4_correction_strict_noop_failure_v1'
                            case_id = $case.Id
                            baseline_pixel_sha256 = $earlyBaseline.PixelSha256
                            zero_strength_pixel_sha256 = $resultRow.PixelSha256
                        } | ConvertTo-Json -Depth 5
                        Write-Utf8File -Path $failurePath -Text $failure
                        throw "Strict no-op failed before corrected generation. Details: $failurePath"
                    }
                }
            }
        }

        $baselineGate = @($resultRows | Where-Object { $_.CaseId -eq $cases[0].Id -and $_.Arm -eq 'baseline' })[0]
        $zeroGate = @($resultRows | Where-Object { $_.CaseId -eq $cases[0].Id -and $_.Arm -eq 'zero' })[0]
        $noOpPassed = $baselineGate.PixelSha256 -eq $zeroGate.PixelSha256

        $modelManifest = @($ModelSpecs | ForEach-Object {
            [ordered]@{ kind = $_.Kind; name = $_.Name; bytes = $_.Bytes; sha256 = $_.Sha256 }
        })
        # Save enough information to reproduce or audit the run without relying on console
        # history: exact graph, sigmas, packages, hardware, hashes, and intervention settings.
        $manifest = [ordered]@{
            schema = $ResultSchema
            run_id = $runId
            completed_utc = [DateTime]::UtcNow.ToString('o')
            mode = $Mode
            source = [ordered]@{
                comfy_commit = $ComfyCommit
                method_bundle = $MethodBundleId
                huggingface_revision = $HuggingFaceRevision
                core_patch_sha256 = (Get-Sha256 -Path $patchPath)
                comfy_core_original_sha256 = $OriginalIdeogramModelSha256
                comfy_core_patched_sha256 = (Get-Sha256 -Path $coreModelPath)
                correction_tensor_sha256 = '5ce873adae5701e9d5f05ebfa8f8b923a1622745c6e9a2bcb3e22fd090ed30c3'
            }
            environment = [ordered]@{
                operating_system = [Environment]::OSVersion.VersionString
                powershell = $PSVersionTable.PSVersion.ToString()
                python = $PythonVersion
                torch = $cuda.torch
                cuda_runtime = $cuda.cuda_runtime
                gpu = $gpu
                comfy_system_stats = $systemStats
                requirements_lock_sha256 = $requirementsLockSha256
                pip_freeze = '../../artifacts/pip-freeze.txt'
            }
            generation = [ordered]@{
                width = 992
                height = 992
                steps = 20
                sampler = 'euler'
                scheduler = 'Ideogram4Scheduler'
                scheduler_parameters = [ordered]@{ mu = 0.5; std = 1.75 }
                sigma_sequence = $sigmas
                guidance = [ordered]@{ base = 7.0; late = 3.0; late_range = @(0.7, 1.0) }
                precision = 'scaled-fp8 checkpoints; ComfyUI loader weight_dtype=default'
                models = $modelManifest
            }
            intervention = [ordered]@{
                target_model = 'conditional only'
                target_step = 0
                target_pass = 0
                operation = 'subtract direction from each image token, then restore that token norm'
                blocks = @(25, 26, 27, 28)
                strength = 0.6
                checkpoint_mutated = $false
            }
            strict_noop = [ordered]@{
                passed = $noOpPassed
                baseline_pixel_sha256 = $baselineGate.PixelSha256
                zero_strength_pixel_sha256 = $zeroGate.PixelSha256
            }
            results = $resultRows.ToArray()
            interpretation = 'Human review is required. Arbitrary prompts are accepted, but a visible change is expected only for prompt/seed pairs that enter the targeted image mode.'
        }
        $resultsPath = Join-Path $runDir 'results.json'
        Write-Utf8File -Path $resultsPath -Text ($manifest | ConvertTo-Json -Depth 50)
        $comparisonPath = Join-Path $runDir 'comparison.html'
        New-ComparisonHtml -Path $comparisonPath -Cases $cases -Results $resultRows.ToArray() -NoOpPassed $noOpPassed

        if (-not $noOpPassed) {
            throw "Strict no-op failed. Results were preserved at $resultsPath for diagnosis."
        }
        Write-Host "Strict no-op passed: $($baselineGate.PixelSha256)"
        Write-Host "Machine-readable results: $resultsPath"
        Write-Host "Comparison page: $comparisonPath"
    }
    finally {
        # This executes after success, PowerShell errors, Ctrl+C, API failure, or an OOM.
        # It restores the parent environment and verifies that the isolated port is closed.
        $env:CUDA_VISIBLE_DEVICES = $previousCudaVisibleDevices
        Stop-IsolatedServer -Process $serverProcess -LocalPort $Port -ExpectedComfyRoot $comfyRoot
    }
}
finally {
    $http.Dispose()
}
