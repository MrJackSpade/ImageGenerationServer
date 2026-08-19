# Image Editing Quality Audit

Status: initial static audit
Date: 2026-08-18
Scope: image-edit, redraw, refine, inpaint, and outpaint workflow graphs; source/output resolution handling; VAE selection and latent sizing; installed checkpoint precision.

This document records the issues found during the initial audit. It deliberately separates confirmed graph/configuration defects from hypotheses that still need controlled renders.

## Executive summary

- No global wrong-VAE-family problem was found. The installed FLUX.1, FLUX.2, Qwen, Mage, Wan, and pixel-space VAE files match their expected architectures, and each inspected graph uses the same VAE for encode and decode.
- Source resolution policy is now explicit: source-sized editors enforce the declared model envelope at submission,
  while MP/bucket/snap editors declare and own their normalization of arbitrary uploads.
- Several edit paths have no minimum working resolution before lossy VAE encoding. A small source can therefore collapse to a very small latent grid before sampling, causing fine identity, texture, text, and edge information to be reconstructed differently even when the requested edit should preserve it.
- FLUX.2 redraw workflows use the generic `KSampler`/`simple` scheduler instead of FLUX.2's resolution-aware scheduler.
- The installed edit checkpoints are predominantly INT8, FP8, Q5, or Q8 variants. This is a more plausible cross-model quality factor than the VAE files.
- Qwen-Image-Edit is fixed at the 20-step speed preset even though the upstream quality configuration uses 40 steps.

## Issue list

### EDIT-001 — Edit submissions do not enforce the model resolution envelope

- [x] Closed by GitHub issue #313
- Severity: high
- Confidence: high
- Affected area: all edit workflows that do not perform their own source normalization

Generation submission resolves the requested output size and calls `ResolutionGuard.EnsureWithin`. Edit submission identifies the source dimensions and passes them to normalization/building, but it does not apply an equivalent envelope guard or generic source-size normalization.

Evidence:

- Generation guard: `src/ImageGen.Comfy/ComfyClient.cs`, around lines 588–596.
- Edit submission: `src/ImageGen.Comfy/ComfyClient.cs`, around lines 700–718.
- Workflow configuration envelopes therefore describe supported sizes but do not automatically constrain an uploaded edit source.

Impact:

- Oversized, undersized, and off-grid input images can reach the VAE and sampler unchanged.
- The rendered size can diverge from the range advertised by the workflow catalog.
- Quality and runtime become dependent on arbitrary upload dimensions.

Proposed work:

- Define an edit-source resolution policy shared by workflows that do not intentionally own sizing.
- Preserve aspect ratio, clamp to the resolved model envelope, and snap to the required spatial multiple.
- Make output-size behavior explicit: native-sized output, source-sized output, or an optional post-resize.
- Add submission tests for undersized, oversized, odd-sized, landscape, and portrait edit sources.

Resolution:

- `IWorkflow.NormalizesSourceResolution` now explicitly separates source-sized editors from workflows that own an
  MP, model-bucket, or pixel-snap normalization step. Shared normalizing bases declare the contract once, while
  standalone normalizers and the pixel-video decorator declare/forward it directly.
- Still-image edit submission resolves `EtaRenderSize` before graph submission. Source-sized workflows apply the
  same full model-envelope guard as generation, so undersized, oversized, and off-grid uploads fail with the model's
  supported dimensions before a prompt is posted.
- Workflow-normalized editors accept arbitrary raw upload dimensions. Their typed resolver remains authoritative,
  which avoids incorrectly applying generation's rectangular minimum-side rules to an aspect-preserving MP budget;
  the submission boundary still requires the resolver to report positive working dimensions.
- CPU-only coverage includes undersized, oversized, off-grid, landscape, portrait, normalized square/landscape/
  portrait sizes, invalid resolver output, a fake-Comfy end-to-end refusal with no `/prompt` post, and a 1×1
  Ideogram upload that is accepted because the workflow normalizes it before VAE encoding.

### EDIT-002 — Generic redraw defaults to raw source resolution

- [x] Closed by GitHub issue #310
- Severity: high
- Confidence: high
- Affected workflows: FLUX.1 redraw, FLUX.2 redraw, Chroma redraw, and any other `img2img-redraw` configuration with `native_pixels: 0`

`Img2ImgRedrawWorkflow` only scales when `native_pixels` is greater than zero and the source exceeds that budget. Most redraw configurations declare zero, which sends the uploaded image directly into `VAEEncode` at its original dimensions.

Evidence:

- `src/ImageGen.Comfy/Workflows/Edit/Img2ImgRedraw/Img2ImgRedrawWorkflow.cs`, around lines 93–147.
- `configurations/workflows/flux2-dev-redraw.json`, `flux2-klein-4b-redraw.json`, and `flux2-klein-9b-redraw.json` all set `native_pixels` to zero.
- Anima and Photanima already demonstrate the intended budgeted path with approximately 0.9–1.0 MP budgets.

Impact:

- A 4K upload can be VAE-encoded and partially denoised at 4K even when the checkpoint is intended to operate near 1 MP.
- Large sources increase memory and processing time and may produce repetition, padding, texture failure, or weak prompt adherence.
- Two users selecting the same workflow can get materially different behavior solely from source dimensions.

Proposed work:

- Assign a native pixel budget to every redraw configuration.
- Prefer model-specific budgets where known; use an explicit approximately 1 MP fallback otherwise.
- Keep the existing aspect-preserving `/16` path and expose the effective render size in diagnostics/metadata.

Resolution:

- Every shipped `img2img-redraw` configuration now has a positive native pixel budget. Existing Anima/Photanima
  model-specific budgets are retained; the former zero-budget FLUX.1, FLUX.2, and Chroma configurations use an
  explicit 1 MP fallback.
- Generic redraw now normalizes both undersized and oversized uploads to that budget, preserves source aspect ratio,
  snaps both axes to the shared 16-pixel grid, and feeds the normalized image into `VAEEncode`.
- A missing value inherits the shared 1 MP edit budget, while zero is rejected at both schema and typed-parameter
  validation boundaries so raw-resolution behavior cannot silently return.
- ETA sizing uses the same resolver as graph construction. Graph tests cover small square, 4K landscape, portrait,
  Anima/Photanima model-specific budgets, the exact VAE input edge, and rejection of the old zero bypass.

### EDIT-003 — FLUX.2 redraw uses a generic, non-resolution-aware scheduler

- [x] Closed by GitHub issue #315
- Severity: high
- Confidence: high
- Affected workflows: `flux2-dev-redraw`, `flux2-klein-4b-base-redraw`, `flux2-klein-4b-redraw`, and `flux2-klein-9b-redraw`

The generic redraw graph uses `KSampler` and the configured `simple` scheduler for every model family. FLUX.2 provides `Flux2Scheduler`, whose sigma schedule is calculated from the render width and height. The project's dedicated FLUX.2 edit graph and the upstream FLUX.2 edit template both use that resolution-aware scheduler.

Evidence:

- Generic redraw sampler: `src/ImageGen.Comfy/Workflows/Edit/Img2ImgRedraw/Img2ImgRedrawWorkflow.cs`, around lines 129–147.
- Correct dedicated path: `src/ImageGen.Comfy/Workflows/_Shared/Base/Flux2KleinEditBase.cs`, around lines 53–59.
- ComfyUI's `Flux2Scheduler` computes the schedule from `width * height / (16 * 16)`.
- Upstream reference: <https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_flux2_klein_9b_kv_image_edit.json>

Impact:

- The denoising schedule is not adapted to image token count.
- Quality can deteriorate as the source aspect or area moves away from the implicit default.
- This compounds EDIT-002 because raw source resolution is also allowed.

Proposed work:

- Split FLUX.2 redraw into a dedicated workflow path.
- Use `Flux2Scheduler`, `SplitSigmasDenoise`, `KSamplerSelect`, and `SamplerCustomAdvanced` with the encoded source latent.
- Feed the exact normalized render width and height into the scheduler.
- Add graph tests that reject `KSampler`/`simple` for FLUX.2 redraw configurations.

Resolution:

- The four FLUX.2 redraw configurations now carry a locked `flux2_scheduler` structural contract. The shared redraw
  workflow branches on that typed setting rather than inferring architecture from a filename or display name.
- FLUX.2 redraw reads `GetImageSize` from the normalized source created by EDIT-002 and wires those exact dimensions
  into `Flux2Scheduler`.
- The graph uses `CFGGuider`, `KSamplerSelect`, `RandomNoise`, `SplitSigmasDenoise`, and
  `SamplerCustomAdvanced` over the encoded source latent. This preserves distilled guidance at CFG 1 and the
  non-distilled 4B Base configuration's real CFG 5/negative conditioning.
- FLUX.1, Chroma, Anima, and Photanima retain the generic `KSampler` path.
- Tests cover all four FLUX.2 configs, exact normalized-image size wiring, denoise-tail output selection, sampler/
  latent/conditioning edges, locked configuration state, absence of `KSampler`, and non-FLUX.2 isolation.

### EDIT-004 — Krea2 redraw bypasses its declared native resolution range

- [x] Closed by GitHub issue #311
- Severity: high
- Confidence: high
- Affected workflow: `krea2-redraw`

Krea2 redraw sends the raw `LoadImage` output directly to `VAEEncode`. Its configuration declares a 1024–2048 range with a 16-pixel step, but those limits are not applied to the uploaded source.

Evidence:

- `src/ImageGen.Comfy/Workflows/Edit/Krea2Redraw/Krea2RedrawWorkflow.cs`, around lines 62–79.
- `configurations/workflows/krea2-redraw.json`, resolution block around lines 104–108.

Impact:

- Small, very large, or off-grid sources run outside the declared operating envelope.
- The eight-step Turbo pass has little opportunity to recover from a poor input scale.

Proposed work:

- Normalize the source into the Krea2 envelope before VAE encoding.
- Decide whether Krea2 should always target a fixed pixel budget or only downscale oversized sources.
- Add size assertions to graph tests and record the effective render dimensions.

Resolution:

- Krea2 Redraw now always targets a configurable 1 MP working budget, preserving source aspect ratio and snapping to
  the shared 16-pixel grid before `VAEEncode`. This intentionally normalizes both undersized and oversized sources
  instead of retaining the former raw-resolution path.
- The workflow applies its configured 2048 px maximum as a long-edge ceiling after MP normalization, so extremely
  wide or tall inputs stay within the declared upper envelope without changing aspect ratio.
- `native_pixels` and `max_dimension` ship hidden in the Krea2 Redraw settings layer, retaining an explicit
  per-workflow contract that can be adjusted without graph-code changes.
- ETA sizing and graph construction use the same resolver. Tests cover a low-resolution square, 4K landscape,
  portrait, extreme-aspect long-edge cap, Lanczos scaling, and the exact scaled input passed to `VAEEncode`.

### EDIT-005 — Ideogram4 refine uses raw dimensions for both VAE and scheduler

- [x] Closed by GitHub issue #312
- Severity: high
- Confidence: high
- Affected workflow: `ideogram4-refine`

Ideogram4 refine reads the uploaded width and height, feeds the unscaled source to `VAEEncode`, and passes the raw dimensions into `Ideogram4Scheduler`. It does not clamp to 2048 or snap to the declared 16-pixel step.

Evidence:

- `src/ImageGen.Comfy/Workflows/Edit/Ideogram4Refine/Ideogram4RefineWorkflow.cs`, around lines 39–40 and 73–87.
- `configurations/workflows/ideogram4-refine.json`, resolution block around lines 110–114.

Impact:

- Off-grid inputs can make the scheduler's nominal dimensions disagree with the effective VAE latent dimensions.
- Images above the documented maximum are accepted and sampled.
- Edge cropping/rounding and quality can become input-dimension dependent.

Proposed work:

- Normalize and snap the source before both VAE encoding and scheduler sizing.
- Derive scheduler dimensions from `GetImageSize` on the normalized image rather than from upload metadata.
- Add odd-size and over-2048 regression tests.

Resolution:

- Ideogram4 Refine now normalizes both undersized and oversized inputs to a hidden/configurable 1 MP working budget,
  preserves aspect ratio, snaps to 16 pixels, and applies a hidden/configurable 2048 px long-edge ceiling before
  `VAEEncode`.
- A typed `Ideogram4SchedulerFromSize` variant wires width and height from `GetImageSize` on that exact normalized
  image. The scheduler can therefore no longer drift from the pixels passed across the VAE boundary.
- ETA sizing uses the same resolver and configuration values as graph construction.
- Tests cover low-resolution square, 4K landscape, odd portrait, odd landscape, extreme-aspect long-edge cap,
  Lanczos scaling, the exact VAE input edge, the image-size edge, scheduler wiring, and ETA parity.

### EDIT-006 — Checkpoint precision and catalog metadata are inconsistent

- [x] Closed by GitHub issue #316
- Severity: high
- Confidence: medium-high
- Affected workflows: potentially most large image editors

The installed model inventory is dominated by quantized edit checkpoints: INT8 ConvRot, FP8, Q5 GGUF, and Q8 GGUF. This may be required for available VRAM, but it is the strongest shared quality variable across otherwise unrelated model families.

The Qwen catalog is specifically inconsistent:

- `qwen-image-edit.json` describes FP8/near-BF16 quality.
- `configurations/models/qwen-image-edit-2511.json` only matches a `q6` filename.
- The available Qwen 2511 checkpoint observed during the audit is an INT8 ConvRot file.
- The current upstream reference workflow specifies `qwen_image_edit_2511_bf16.safetensors`.

Impact:

- Actual quality can be substantially different from catalog claims.
- Automatic recognition may fail, or a manual database binding may silently select an unexpected file.
- Comparing model families is confounded by different quantization levels.

Proposed work:

- Inspect the active `dbo.ModelBinding` rows when the service/database is available.
- Correct model match expressions and catalog precision descriptions.
- Record checkpoint basename, loader, file dtype/quantization, VAE, and text encoder in render metadata.
- Run fixed-seed A/B comparisons for BF16/FP8 versus the installed quantized variant where hardware permits.

Resolution:

- The Qwen Image Edit 2511 model slot now recognizes BF16, FP16, FP8, INT8/ConvRot, and Q2-Q8 published-name
  variants. Matching remains suggestion-only: one unambiguous file may fill an empty slot, multiple installed
  precisions require a user choice, and an existing `dbo.ModelBinding` is never overwritten.
- The Qwen workflow card no longer asserts that every machine is running FP8. It identifies checkpoint precision as
  machine-bound and scopes the existing timing measurement to the observed INT8 ConvRot binding.
- Every submitted render now snapshots a model manifest containing portable checkpoint/VAE/text-encoder basenames,
  loader mode, requested weight dtype, and a conservative filename-derived quantization hint. The snapshot persists
  on `dbo.JobSlot` and is returned as `models` by Generation Values, so later binding changes cannot rewrite an
  image's provenance.
- SQLite and SQL Server migrations add the nullable manifest column without changing existing rows. Tests cover
  variant recognition, multi-precision ambiguity, manifest inference, persistence, migration, and API projection.
- No active binding was changed and no hardware-dependent A/B render was run; those remain manual validation steps
  for the operator who has the relevant checkpoints and GPU.

### EDIT-007 — Qwen-Image-Edit is fixed at the 20-step speed preset

- [x] Closed by GitHub issue #318
- Severity: medium-high
- Confidence: high
- Affected workflows: `qwen-image-edit` and any configuration inheriting the same hidden step value

The Qwen workflow hides a 20-step setting. The official Qwen 2511 blueprint documents 20 as the faster Comfy preset and 40 as the quality-oriented setting; its current sampler is configured for 40 steps.

Evidence:

- `configurations/workflows/qwen-image-edit.json`, lines 22–24.
- Upstream reference: <https://github.com/Comfy-Org/ComfyUI/blob/master/blueprints/Image%20Edit%20%28Qwen%202511%29.json>

Impact:

- Qwen quality is intentionally traded for speed without a visible choice.
- The quality loss may be amplified by an aggressively quantized checkpoint.

Proposed work:

- Make steps user-selectable or add explicit Fast and Quality configurations.
- Use 40 steps for the quality/default path and retain 20 as a documented speed option.
- Benchmark 20 versus 40 using the same source, prompt, seed, and checkpoint.

Resolution:

- `qwen-image-edit` and its masked companion now ship with the upstream-recommended 40-step quality default.
- The parameter remains hidden in the render UI and configurable through the existing workflow settings system.
- FireRed and LongCat configurations retain their model-specific step counts.
- Graph tests verify both Qwen configurations retain hidden settings and wire 40 steps into their sampler. Hardware
  benchmarking remains a manual validation step.

### EDIT-008 — FLUX.2 Klein edit uses a coarser resolution grid than upstream

- [ ] Open
- Severity: low-medium
- Confidence: high that the difference exists; medium that it materially hurts quality
- Affected workflows: editors derived from `Flux2KleinEditBase`

The project correctly scales sources and references to approximately 1 MP, but rounds both dimensions to a 64-pixel grid. The current official FLUX.2 template uses `resolution_steps: 1`.

Evidence:

- `src/ImageGen.Comfy/Workflows/_Shared/Base/Flux2KleinEditBase.cs`, lines 13–18 and 28–30.
- Upstream reference: <https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_flux2_klein_9b_kv_image_edit.json>

Impact:

- The coarse snap can alter aspect ratio and pixel area more than necessary.
- Extreme aspect ratios are affected more strongly than common photographic ratios.

Proposed work:

- A/B test steps 64, 16, and 1.
- Prefer the smallest step consistent with the model/VAE latent requirements.
- Retain scheduler and empty-latent dimensions from `GetImageSize` so every graph consumer remains aligned.

### EDIT-009 — Output resolution behavior is inconsistent across editor families

- [ ] Open
- Severity: medium
- Confidence: high
- Affected area: cross-workflow API/UI behavior

Different editors currently produce different notions of output size:

- Qwen-family editors use a preferred approximately 1 MP bucket selected by `FluxKontextImageScale`.
- FLUX.2 Klein edit targets approximately 1 MP on a 64-pixel grid.
- Generic redraw and Krea2 redraw usually inherit raw source dimensions.
- Ideogram4 refine uses raw source metadata.
- Some inpaint/outpaint workflows have explicit maximum-dimension policies.

Impact:

- Users cannot predict whether an edit preserves exact source dimensions.
- Multi-turn editing can repeatedly resize images as they cross workflow families.
- Resolution changes can be mistaken for VAE degradation.

Proposed work:

- Define and document one output-size contract per workflow: exact source, normalized native, expanded canvas, or explicit requested output.
- Return effective input, latent, and output dimensions in job metadata.
- If exact source dimensions are required, perform a single documented final resize rather than changing latent sizing implicitly.

### EDIT-010 — No isolated VAE round-trip diagnostic exists

- [x] Closed by GitHub issue #319
- Severity: medium
- Confidence: high
- Affected area: diagnostics

The current system does not provide a simple source → VAE encode → VAE decode comparison independent of diffusion sampling. Without that control, VAE reconstruction loss, model quantization, resizing, and sampling failures are visually conflated.

Impact:

- Reports of blur, color shift, texture loss, or dimension drift cannot quickly be attributed to the VAE.
- VAE regressions can pass graph-structure tests.

Proposed work:

- Add a developer-only VAE round-trip workflow or diagnostic command.
- Record source/output dimensions and compute PSNR, SSIM, and a perceptual metric where practical.
- Test each installed VAE at representative square, landscape, portrait, odd, and oversized inputs.
- Use the pixel-space identity VAE as a control. If the associated workflow is also poor, the VAE is not the cause.

Resolution:

- Added a reusable `vae-roundtrip` edit workflow whose complete graph is `LoadImage -> VAELoader -> VAEEncode ->
  VAEDecode -> SaveImage`. It has no resize, checkpoint, text encoder, conditioning, noise, or sampler.
- Shipped `vae-roundtrip-qwen` as the first API-oriented diagnostic configuration. Additional VAE controls can reuse
  the same workflow by binding a different VAE requirement in configuration.
- The workflow takes no prompt and opts out of the semantic-edit no-change gate, so a near-identical reconstruction
  is retained for comparison. The render manifest records the bound VAE basename and normal job metadata records
  source/output identity and output dimensions.
- Tests pin the exact five-node topology and absence of sampling/resizing. GPU execution and image-quality metrics
  remain manual validation in the environment that has the installed VAE and renderer.

### EDIT-011 — Existing tests validate graph intent, not rendered quality

- [x] Closed by GitHub issue #320
- Severity: medium
- Confidence: high
- Affected area: test coverage

The focused workflow, budget-scale, model-matcher, and model-reference tests pass (164 tests during this audit). They confirm that the emitted graphs match the repository's current expectations, but those expectations include the resolution and scheduler choices above.

Impact:

- A graph can be structurally valid while using a poor scheduler, unsupported resolution, or low-quality checkpoint.
- Upstream workflow drift is not detected.

Proposed work:

- Add semantic graph invariants, such as requiring `Flux2Scheduler` for FLUX.2 and normalized dimensions for workflows with declared envelopes.
- Add small fixed-seed render fixtures when a GPU test environment is available.
- Track image similarity separately for preservation edits and instruction adherence for destructive edits.

Resolution:

- Added a catalog-wide invariant that discovers every FLUX.2 redraw configuration from its requirement links and
  requires `Flux2Scheduler` while rejecting the legacy `BasicScheduler`/`KSampler` path. New variants are covered
  without extending a fixed test list.
- Added a catalog-wide resolution invariant for still editors whose sizing contract reports a normalized working
  size. Oversized inputs must resolve to positive dimensions inside the model envelope and on its declared step grid.
- The structural invariants complement the focused graph tests added by the preceding audit fixes. Fixed-seed image
  similarity and instruction-adherence scoring still require a GPU render environment and remain manual validation.

### EDIT-012 — Runtime unavailability was an audit limitation, not a product defect

- [x] Closed as not requiring a product change
- Severity: audit note
- Confidence: high

The initial audit could not inspect active `dbo.ModelBinding` rows or renderer logs because the Docker deployment was stopped. That does not block correction of the repository's workflow-construction, resolution, scheduler, configuration, and test defects. Those behaviors are deterministic in the emitted graph and can be implemented and verified with local unit tests without starting Docker or requiring a GPU.

Live bindings and controlled GPU renders remain useful when diagnosing a specific installed environment, but they are not a prerequisite for the fixes in this list and are not themselves a repository issue.

Resolution:

- Treat the checked-in workflow configuration as the implementation target.
- Verify every correction through focused graph/configuration tests and the normal local test suite.
- Reserve live binding/log inspection and fixed-seed GPU comparisons for follow-up only when a repository-level test cannot distinguish the behavior.

### EDIT-013 — Remaining inpaint/outpaint editors lack a compression-aware working resolution

- [x] Closed by GitHub issue #309
- Severity: critical for low-resolution inputs
- Confidence: high
- Affected workflows: Krea2 AnyPaint, Anima inpaint/outpaint, FLUX Fill, and base-Qwen InstantX inpaint/outpaint

These workflows either pass the source directly to the VAE or apply only a maximum-size cap. They do not upscale a source that is below the model's useful working range. The problem is not an incorrect VAE file: it is too little spatial information entering a lossy codec. For example, a 512×512 source produces a 64×64 grid in a true 8× VAE, a 32×32 grid in a true 16× VAE, and a 16×16 grid in a true 32× VAE. At 256×256 those grids fall to 32×32, 16×16, and 8×8 respectively.

Generic redraw, Krea2 Redraw, and Ideogram4 Refine have dedicated findings (EDIT-002, EDIT-004, and EDIT-005) and remain separate so each correction receives its own issue and commit.

Compression terminology matters:

| Family | Lossy spatial VAE encode | Model-facing spatial grid | Notes |
| --- | ---: | ---: | --- |
| FLUX.1 / Qwen Image / Wan 2.1 | 8× | commonly 16× after model patching/packing | The second 2× reduction is tokenization/rearrangement, not another VAE reconstruction loss. |
| FLUX.2 | 8× | 16× after 2×2 latent packing | The installed VAE exposes a packed 128-channel latent, but packing rearranges the underlying 32-channel 8× representation rather than discarding another level of pixels. |
| Mage Flow | 16× | 16× | This is a true 16× codec. |
| Hunyuan Image 2.1 | 32× | 32× | This is the true 32× still-image VAE in the installation; it is not used by most instruction editors. |
| Pixel-space | 1× | 1× | Identity/control path. |

Upscaling cannot recreate frequencies that are already absent from the uploaded source. It can, however, prevent the VAE and editing model from imposing *additional* loss by giving existing contours, colors, and structures more latent cells. This is especially relevant for preservation edits, where codec reconstruction changes can look like unwanted model modifications.

Resolution:

- Added `EditWorkingResolution`, which maps small, native, and oversized canvases to a shared aspect-preserving 1 MP native budget on a 16-pixel grid while retaining an optional long-edge safety cap.
- FLUX Fill, base-Qwen InstantX, Anima, and Krea2 AnyPaint now scale the complete image/mask canvas to one resolved target before VAE/custom encoding.
- Inpaint/outpaint grow, blur, ControlNet, latent-mask, and composite consumers remain aligned to the same resized mask.
- ETA/effective render sizing uses the same resolver as each emitted graph.
- Added low-resolution inpaint/outpaint graph regressions across all eight configurations plus independent budget/cap tests.
- Output is produced at the selected working MP budget with the source/padded-canvas aspect ratio preserved. It is not silently resized back to the upload's original pixel dimensions.

### EDIT-014 — Add hidden, configurable edit-quality megapixel presets

- [ ] Open
- Severity: feature / final rollout item
- Confidence: high
- Dependency: implement after the workflow-specific quality defects above are corrected

Add an edit-page `Quality` selector that controls the internal image-editing megapixel budget. It must not select fixed resolutions: the uploaded source aspect ratio remains unchanged, and the chosen per-workflow MP budget is converted into working width/height and then snapped to the workflow's supported dimension step.

Required behavior:

- The selector has exactly three choices: `Low`, `Medium`, and `High`.
- `Medium` is the default when the client submits no override.
- The control is `hidden` by default, using the existing revealable parameter-visibility system rather than a new visibility mechanism.
- Any user can reveal or hide the selector through the existing per-workflow settings UI.
- Each workflow configuration provides independently editable MP budgets for `Low`, `Medium`, and `High`; the labels are shared, but their numeric values may differ by model/VAE family.
- The workflow settings UI exposes all three numeric MP defaults and the selector's visibility. Machine-level overrides continue to fall back to the shipped values when reset.
- A shared selection can fan out across multiple editors: for example, `High` resolves independently against each selected workflow's own `High` MP budget.
- The selected budget scales the source image, mask, and all spatial reference inputs together before VAE encoding. It does not change aspect ratio.
- The selected MP budget determines the output dimensions after aspect-preserving, model-step snapping; it does not revert to the upload's original pixel dimensions.
- API callers may override the quality selection even when the control remains hidden, consistent with existing `hidden` parameter semantics.

Suggested configuration shape:

```json
{
  "edit_quality": {
    "value": "medium",
    "visibility": "hidden",
    "megapixels": {
      "low": 0.5,
      "medium": 1.0,
      "high": 2.0
    }
  }
}
```

The exact shipped MP values must be chosen per workflow from its supported/native operating range and verified by the resolution/VAE comparison matrix. They should not be assumed to be universally 0.5/1.0/2.0 MP.

Acceptance coverage:

- Catalog parsing, validation, overrides, and reset-to-shipped behavior for all three budgets.
- Hidden-by-default and per-user reveal/hide behavior on the edit page.
- Default-to-`Medium` behavior for UI and API requests that omit the selection.
- Correct aspect preservation, dimension snapping, mask/reference alignment, and per-workflow resolution when one selection fans out to multiple editors.
- Output pixel count follows the selected MP budget while preserving the source or padded-canvas aspect ratio.

## VAE findings that are not currently issues

These observations should remain documented so later work does not replace correct VAE files unnecessarily:

- FLUX.1 workflows bind a FLUX.1 AE/16-channel VAE.
- FLUX.2 and Ideogram4 workflows bind the 32-channel FLUX.2 VAE.
- Qwen, Krea2, FireRed, and Anima bind the Qwen Image VAE where configured.
- Mage workflows bind the Mage Flow VAE.
- Wan-derived workflows bind the Wan 2.1 VAE.
- `pixel_space_vae.safetensors` is intentionally a tiny identity/pixel-space marker, not a corrupt conventional VAE.
- Inspected workflows use the same VAE instance for source encoding and final decoding.
- Qwen's `FluxKontextImageScale` preprocessing is also present in the current official Qwen 2511 blueprint. It changes output dimensions to a preferred bucket but is not a local upstream-parity defect.

## Recommended comparison matrix

Use the same lossless source PNG, prompt, and seed for each comparison.

1. VAE-only round trip for every VAE family.
2. Qwen 20 steps versus 40 steps.
3. Qwen INT8/quantized checkpoint versus FP8 or BF16.
4. FLUX.2 redraw generic `simple` scheduler versus `Flux2Scheduler`.
5. Raw source resolution versus normalized approximately 1 MP resolution, including direct round trip versus upscale → VAE round trip → downscale.
6. FLUX.2 Klein resolution steps 64 versus 16 versus 1.
7. Square, 3:2 landscape, 2:3 portrait, extreme aspect, odd-sized, and 4K sources.
8. Pixel-space VAE redraw as the no-VAE-loss control.

## Suggested implementation order

1. EDIT-013, EDIT-001, EDIT-002, EDIT-004, and EDIT-005: establish and apply compression-aware minimum/maximum working-resolution policies.
2. EDIT-003: correct the FLUX.2 redraw scheduler.
3. EDIT-006 and EDIT-007: correct checkpoint metadata/bindings and add Qwen quality settings.
4. EDIT-010 and EDIT-011: add diagnostics and semantic/render tests.
5. EDIT-008 and EDIT-009: tune grid size and formalize output-resolution contracts.
6. EDIT-014: cap the remediation with the hidden, per-workflow configurable `Low`/`Medium`/`High` edit-quality MP selector.
