namespace ImageGen.Comfy;

/// <summary>
/// MiniMax-H3 — an omni-modal video model with NATIVE stereo audio (voice/SFX/music generated in the same forward
/// pass, not layered on after). A single "fl2va" diffusion model serves both text→video and image→video; the two
/// differ only in whether a first frame is fed to the one H3-specific node, <c>MiniMaxH3ImageToVideo</c>, which
/// encodes the prompt itself (no separate CLIPTextEncode) and emits (positive, latent). Distilled sampling
/// (BasicGuider, no CFG/negative) through a res_multistep SamplerCustomAdvanced chain, exactly like the official
/// ComfyUI templates (video_minimax_h3_{t2v,i2v}.json).
///
/// <para>UNLIKE every other video model here, H3 does NOT end in the silent <c>SaveAnimatedWEBP</c> — its whole
/// point is the audio, which WEBP cannot carry. The video latent decodes to frames and the SAME latent decodes to
/// audio; <c>CreateVideo</c> muxes them and <c>SaveVideo</c> writes a real mp4 with a baked-in stereo track. The
/// render pipeline stores/serves that mp4 by content-type (see <c>RenderOrchestrator.RunSlotAsync</c> +
/// <c>/image/{id}/mp4</c>), audio intact.</para>
///
/// <para>Weights: the int8-ConvRot diffusion + int8-ConvRot Qwen3-VL 32B text encoder (native tensor-core INT8 on
/// Ampere; the upstream template's nvfp4_awq encoder is Blackwell-only), both loaded through the plain
/// <c>UNETLoader</c>/<c>CLIPLoader</c> that read the embedded ConvRot metadata. Requires ComfyUI ≥ v0.30.1, which
/// adds <c>MiniMaxH3ImageToVideo</c> and the CLIPLoader <c>minimax</c> type.</para>
/// </summary>
/// <summary>Which H3 task the shared graph builds: text→video (no source), image→video (source is the first frame),
/// or reference→video (source + picker images condition the subject/identity, never a first frame).</summary>
internal enum H3Mode { T2V, I2V, Ref2V }
