# Ideogram 4 correction reproduction

This directory contains a clean-room bootstrap for the frozen Ideogram 4 early-denoise
correction. It builds an isolated ComfyUI runtime and does not modify an
installed ComfyUI or any checkpoint.

Distribute this entire directory, including `assets`. The 2.36 MB frozen direction tensor,
ComfyUI node, core patch, and metadata are included locally and verified before any download.
The bootstrap does not access or require the application repository where the research was
originally developed.

## Quick start

1. Open `reproduction.config.psd1` in any text editor.
2. Replace or add entries under `Prompts` and `Seeds`.
3. Open PowerShell in this directory and run:

```powershell
.\Reproduce-Ideogram4Correction.ps1 -PreflightOnly
.\Reproduce-Ideogram4Correction.ps1 -AcceptModelLicense
```

The config file is deliberately short and heavily commented. Its default `Custom` mode runs
every configured prompt with every configured seed. Command-line values override the config
when automation needs them.

The first command checks Windows, the NVIDIA driver, GPU memory, disk space, the chosen
port, local model-cache hashes when supplied, and all download endpoints. It downloads
nothing.

The second command downloads approximately 29.5 GB of model files plus the isolated
runtime. Read the [Ideogram model agreement](https://huggingface.co/ideogram-ai/ideogram-4-fp8/blob/main/LICENSE.md)
before using `-AcceptModelLicense`.

The exact workflow was tested on a 24 GB NVIDIA GPU. `-AllowUntestedVram` permits an
attempt on another CUDA configuration, but does not turn it into a validated one.

## Run your own prompts

The normal path is editing these two arrays in `reproduction.config.psd1`:

```powershell
Prompts = @(
    'a ceramic teapot beside a folded linen napkin'
    'a stone footbridge over a shallow woodland creek in autumn'
)

Seeds = @(
    12345
    67890
)
```

Then the only runtime argument required is the model-license acknowledgement:

```powershell
.\Reproduce-Ideogram4Correction.ps1 -AcceptModelLicense
```

For one-off automation, prompts and seeds can still override the config on the command line:

```powershell
.\Reproduce-Ideogram4Correction.ps1 -Mode Custom `
  -Prompt 'a ceramic teapot beside a folded linen napkin' `
  -Seed 12345,67890 `
  -AcceptModelLicense
```

Alternatively, set `Prompts = @()` and `PromptFile = 'prompts.example.txt'` in the config.
Blank lines and lines beginning with `#` in that UTF-8 file are ignored. The equivalent
command-line override is:

```powershell
.\Reproduce-Ideogram4Correction.ps1 -Mode Custom `
  -PromptFile .\prompts.example.txt `
  -Seed 12345,67890 `
  -AcceptModelLicense
```

Every custom prompt is crossed with every supplied seed. Each case receives an unmodified
baseline and a corrected generation. The first case also receives a zero-strength graph;
the script stops with an error unless its decoded pixels exactly equal the baseline.

The executable script contains no built-in prompt or seed dataset. Custom inputs live in
`reproduction.config.psd1` (or an optional prompt text file). The historical benchmark lives
separately in `frozen-validation.cases.psd1`, so it can be inspected, cited, or replaced
without searching through setup code.

## Re-run the held-out panel

```powershell
.\Reproduce-Ideogram4Correction.ps1 -Mode FullValidation -AcceptModelLicense
```

This runs the fixed 16-case panel: eight historical artifact cases and eight same-prompt
reference-clean cases. The single-direction strength is pending fixed-panel revalidation,
so the script records outputs without assigning an automatic success label. `Smoke` selects
one declared case from the same manifest; `Custom` does not load the frozen manifest at all.

If the four model files already exist, they can be verified and referenced read-only:

```powershell
.\Reproduce-Ideogram4Correction.ps1 -ExistingModelRoot 'D:\ComfyUI\models' `
  -AcceptModelLicense
```

## What is pinned

- ComfyUI commit `62b3c94bd45154f6486c7abf1b9efcacee96ea69`.
- Comfy-Org/Ideogram-4 revision `bbee2ab2b14b2b5223448d12d6e31e5f9cec0546`.
- Python 3.12.10 and PyTorch 2.12.0 with the CUDA 13.0 wheel set.
- Exact SHA-256 and byte length for all four checkpoints, the core patch, node code,
  metadata, and the frozen 2.36 MB direction tensor.
- 992×992, 20 Euler steps, Ideogram4Scheduler (`mu=0.5`, `std=1.75`), base guidance 7,
  and late guidance 3 over the last 30 percent.

The correction edits only the in-memory conditional-model residual stream during the first
denoising pass. It uses blocks 25–28 at strength 0.6. Each edited image token has its
original norm restored. The separate unconditional model and all checkpoint files remain
unchanged.

## Outputs and cleanup

Each run writes a new timestamped directory under `runs` containing:

- `results.json`: prompts, seeds, model hashes, package versions, sigma sequence, guidance,
  interventions, workflows, output hashes, and the strict no-op result;
- `comparison.html`: a local baseline/zero/corrected comparison page;
- `images` and `workflows`: the exact outputs and API graphs;
- ComfyUI stdout and stderr logs.

The server listens only on `127.0.0.1` and is always stopped before the script exits. Setup
is resumable: verified completed files are reused and large `.partial` downloads continue by
HTTP range. The script never deletes an existing environment or output.

## Scope

This reproduces the current inference method with a frozen, model-specific direction. It
accepts arbitrary descriptive prompts, but not every prompt/seed pair enters the targeted
image mode, so not every pair should visibly change.

Adapting the methodology to another model is a separate measurement task: collect fixed
prompt/seed target and reference-clean cases, capture spatial residuals at early timesteps,
fit candidate directions without collapsing all image tokens to one mean, exclude clean
variation, validate against matched-norm random directions, and freeze the candidate before
held-out paired testing. A predictive linear probe alone is not causal evidence.
