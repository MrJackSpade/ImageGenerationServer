namespace ImageGen.Comfy;

/// <summary>
/// One way of running ONE model: a self-contained ComfyUI graph builder. A workflow owns its node topology
/// explicitly (no central dispatch), declares the full set of parameters it understands (<see cref="Schema"/>),
/// and the VRAM band the machine must satisfy for it to be offered. Exactly one model per workflow — two models
/// never share a workflow class. A <see cref="WorkflowConfiguration"/> binds to a workflow by <see cref="Name"/>,
/// fills in its parameters, and is what the API actually exposes.
/// </summary>
public interface IWorkflow
{
    /// <summary>Stable workflow id a configuration binds to (conventionally equal to its sole configuration's id,
    /// e.g. "pony-v6", "flux1-kontext").</summary>
    string Name { get; }

    /// <summary>Generate (text→image) or Edit (image + instruction). Determines /generate vs /edit eligibility.</summary>
    WorkflowKind Kind { get; }

    /// <summary>What this workflow outputs — a still image or a video clip. Groups the edit dropdown by ability.</summary>
    WorkflowMedia Media { get; }

    /// <summary>What this workflow CONSUMES as its source — a still image (the default for every editor) or a video
    /// clip (video-to-video). Almost every edit workflow edits an image; only the deterministic pixel-quantize V2V
    /// pass takes a clip. The edit UI reads this to offer a video-source workflow ONLY when the source is a clip, and
    /// to keep image editors off a video source. <see cref="ComfyClient.SubmitEditAsync"/> reads it to upload the
    /// source as a real video file (transcoding an animated webp to mp4) and load it with <c>LoadVideo</c> rather than
    /// <c>LoadImage</c>. Irrelevant for generate workflows.</summary>
    WorkflowMedia SourceMedia => WorkflowMedia.Image;

    /// <summary>For video editors: whether the text prompt actually directs the MOTION (true for real video models
    /// like Wan/LTX) or only sets the scene while motion is generic/automatic (false for AnimateDiff). Drives honest
    /// UI wording so we don't promise motion control AnimateDiff can't deliver. Irrelevant for image workflows.</summary>
    bool PromptDirectsMotion { get; }

    /// <summary>For image editors: what the prompt DESCRIBES — a change instruction, the whole resulting picture, or
    /// the content of the masked region (see <see cref="Comfy.PromptSemantics"/>). Drives honest UI wording — a redraw
    /// user must not be told to "describe a change", and a FLUX-Fill user must not be told to describe the whole
    /// picture (that renders the whole scene into the hole). Default: an image editor takes an instruction.
    /// Irrelevant for video workflows, whose wording is driven by <see cref="PromptDirectsMotion"/>.</summary>
    PromptSemantics PromptSemantics => PromptSemantics.Instruction;

    /// <summary>Image→video only: whether this workflow accepts an optional LAST frame (the source image is the first
    /// frame; the clip is interpolated to end on the supplied image). When true, an API caller may set
    /// <c>EditRequest.LastFrameImageId</c> and the build wires <c>WanFirstLastFrameToVideo</c>. Surfaced on the
    /// <c>/workflows</c> row (<c>supportsLastFrame</c>) so callers can discover it. Default false.</summary>
    bool SupportsEndFrame => false;

    /// <summary>Video only: whether the clip carries an AUDIO track (MiniMax-H3 generates native stereo audio in the
    /// same pass, saved as an mp4 with sound). Surfaced on the <c>/workflows</c> row (<c>hasAudio</c>) so the
    /// composer/edit UI can offer an unmute control for a clip that actually has sound, without adding chrome to the
    /// silent clips every other video model produces. Default false.</summary>
    bool HasAudio => false;

    /// <summary>True when this workflow INTENTIONALLY preserves the source image's composition rather than performing
    /// a semantic edit — e.g. inpaint (only a masked region changes) or the pixel transforms (restyle to a fixed
    /// grid+palette). The JobQueue's no-change gate (a whole-image perceptual-hash diff built to catch instruction
    /// editors that silently decline) would mistake such an output for a no-op and discard it, so these workflows
    /// opt out of the gate. Default false: a normal edit must visibly change the image to be kept.</summary>
    bool PreservesComposition => false;

    /// <summary>False for a workflow that runs WITHOUT a diffusion model — e.g. the pure-CPU quantizer, which has
    /// no checkpoint requirement by design. The catalog's "no checkpoint = misconfigured, hide it" guard must not
    /// drop such a workflow. Default true: a normal workflow needs a model file.</summary>
    bool RequiresModel => true;

    /// <summary>False for a workflow whose graph has NO text encoder, so a prompt has nowhere to go — the upscalers.
    /// The editor hides its instruction box entirely rather than inviting text it would silently discard. Default
    /// true: a normal editor consumes the instruction.</summary>
    bool TakesPrompt => true;

    /// <summary>Every parameter this workflow understands, with type/default/range. The contract a configuration
    /// selectively fills and exposes.</summary>
    IReadOnlyList<ParamSpec> Schema { get; }

    /// <summary>The model's stepped frame-count rule (valid clip length = Base + k*Step), or null for stills /
    /// models that accept any length. Metadata read at enqueue by <see cref="Normalize"/>. Stepped video models
    /// (LTX = (1,8), Wan = (1,4)) set it; everything else leaves it null.</summary>
    FrameRule? FrameRule => null;

    /// <summary>An explicit output-resolution envelope used by the pixel-art render-size snap for a model whose config
    /// links no checkpoint (so <see cref="ResolvedRequirements.Resolution"/> is null) — e.g. the self-contained
    /// DreamOmni2 pipeline. Null (default) means "use the resolved checkpoint's resolution"; only such pipelines set it.</summary>
    ModelResolution? ResolutionEnvelope => null;

    /// <summary>Pre-build parameter validation/normalization — the single place input clamping lives. Runs TWICE:
    /// at ENQUEUE with <see cref="NormalizeContext.Empty"/> (params only — the frame-count snap, whose notice reaches
    /// the placeholder card before the render starts), and again at SUBMIT with the source dims + resolved
    /// requirements (<see cref="NormalizeContext.AtSubmit"/>) for snaps that need them. May MUTATE <paramref name="p"/>
    /// in place; returns one human-readable notice per USER-VISIBLE change (yellow text on the cards). Snapping rather
    /// than hard-rejecting is intentional — it keeps a mixed-model batch (each model with its own rule) flowing.</summary>
    IReadOnlyList<string> Normalize(IDictionary<string, object?> p, NormalizeContext ctx)
    {
        List<string> notices = new List<string>();

        // Frame-count snap (stepped video models). Param-only → fires on BOTH passes; idempotent, so the submit pass
        // is a no-op once enqueue has already snapped. This is the one that produces a user notice.
        if (FrameRule is { } fr && p.TryGetValue(WorkflowParamKeys.Length, out object? raw) && raw is not null)
        {
            int req = ParamsCodec.AsInt(raw);
            if (req > 0)
            {
                int snapped = fr.Snap(req);
                if (snapped != req)
                {
                    p[WorkflowParamKeys.Length] = snapped;
                    notices.Add($"{req} frames isn’t valid for this model — rendering {snapped} (frame count must be {fr.Step}n+{fr.Base}).");
                }
            }
        }

        return notices;
    }



    /// <summary>Build the ComfyUI graph as a typed <see cref="ComfyWorkflowGraph"/> (node-id → typed node). It stays
    /// typed until the renderer serializes it to <c>/prompt</c>. <paramref name="p"/> is the merged parameter bag —
    /// the implementation deserializes it into its own typed params DTO at the <see cref="ParamsCodec"/> boundary
    /// before building; <paramref name="req"/> the resolved requirement filenames, <paramref name="inputs"/> the
    /// runtime prompt/aspect/image data.</summary>
    ComfyWorkflowGraph Build(IReadOnlyDictionary<string, object?> p, ResolvedRequirements req, WorkflowInputs inputs);
}
