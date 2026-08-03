using System.Text.Json;
using ImageGen.Application.Rendering;

namespace ImageGen.Comfy;

/// <summary>The runtime kind of a workflow: pure text-to-image generation, or an image edit (image + instruction).
/// Drives whether a configuration is offered to /generate vs /edit and how its inputs are populated.</summary>
public enum WorkflowKind { Generate, Edit }

/// <summary>What a workflow produces — a still image or an animated clip (image → video). Lets the UI group the
/// edit dropdown by image-editing vs video so the user knows what an editor outputs before picking it.</summary>
public enum WorkflowMedia { Image, Video }

/// <summary>What an image editor's prompt actually DESCRIBES, driving honest UI wording and API prompting guides.
/// <see cref="Instruction"/>: a change to make ("add a red party hat" — Kontext/Qwen-Edit style).
/// <see cref="WholeImage"/>: a generation-style prompt for the whole resulting picture (redraw; denoise-style
/// inpaint on tag models, whose conditioning is whole-image by construction).
/// <see cref="MaskedRegion"/>: what should appear IN the masked region (FLUX.1 Fill style — its official examples
/// prompt the patch content; a whole-scene prompt at Fill's guidance 30 gets the whole scene rendered INTO the
/// hole, measured at −60 luminance levels on a sky fill vs −6 for a region prompt).</summary>
public enum PromptSemantics { Instruction, WholeImage, MaskedRegion }

/// <summary>The CLR type of a workflow parameter, so a configuration value can be coerced + a UI control chosen.</summary>
public enum ParamType { Int, Double, String, Bool, Enum }

/// <summary>A stepped frame-count rule for video models: the only valid clip lengths are <c>Base + k*Step</c>
/// (k ≥ 0) — LTX = (1, 8) → 1, 9, 17, …, 97; Wan = (1, 4) → 1, 5, 9, …. It's a property of the model's VAE temporal
/// compression, mirrored from the underlying ComfyUI node's <c>length</c> step. Null on a workflow means no frame
/// constraint (stills / unconstrained models). Read at enqueue (see <see cref="IWorkflow.Normalize"/>) to snap an
/// out-of-range requested length onto the nearest valid value and surface a notice — so a bad value neither crashes
/// the model nor hard-rejects the job (which would block a mixed-model batch where each model has its own rule).</summary>
public sealed record FrameRule(int Base, int Step)
{
    /// <summary>The smallest valid length that is &gt;= <paramref name="n"/> — i.e. round UP to the next
    /// <see cref="Base"/> + k*<see cref="Step"/>. Always rounding up (never down) means the snap never renders FEWER
    /// frames than asked, and is consistent across step sizes (30 → 33 for both an 8n+1 and a 4n+1 model). Already-valid
    /// values are returned unchanged.</summary>
    public int Snap(int n)
    {
        if (Step <= 0 || n <= Base) return Base >= n ? Base : n;
        int k = (int)Math.Ceiling((n - Base) / (double)Step);
        return Base + k * Step;
    }
}

/// <summary>
/// One parameter a <see cref="IWorkflow"/> understands — the workflow's declaration of an available setting.
/// A <see cref="WorkflowConfiguration"/> supplies a value for it (and may expose it to the UI). The workflow's
/// schema is the full menu of parameters; the configuration is the selective, defaulted view of that menu.
/// </summary>
public sealed class ParamSpec
{
    public required string Key { get; init; }
    public required ParamType Type { get; init; }
    /// <summary>Workflow-level fallback used when a configuration doesn't set this key.</summary>
    public object? Default { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>UI numeric-input increment for an exposed int/double control. Null → the frontend's per-type default
    /// (1 for int, 0.1 for double). Set it to match the value's precision so the default is reachable (e.g. a 0.35
    /// default on a 0.1 step is not — it needs 0.01).</summary>
    public double? Step { get; init; }
    /// <summary>Enum.</summary>
    public string[]? Choices { get; init; }
    /// <summary>UI label when exposed.</summary>
    public string? Label { get; init; }
    public string? Help { get; init; }

    /// <summary>
    /// True when this parameter's value is a MODEL SLOT ID, not a literal value — resolved to the filename this
    /// machine has bound to that slot before the graph is built.
    ///
    /// <para>These used to hold filenames directly, which meant a second set of the author's filenames living in
    /// the configurations, outside the binding system and unchangeable by a user. Declaring the reference is
    /// deliberate rather than inferring it from the value: a rule like "resolve anything that looks like a slot
    /// id" silently rewrites any parameter whose value happens to collide.</para>
    /// </summary>
    public bool IsModelRef { get; init; }

    /// <summary>True when this parameter's value materially drives render TIME, so its merged value is captured with
    /// each timing sample and used to param-match the ETA (steps, frame count). Resolution is a time driver too but is
    /// captured from the RESOLVED render (w,h) — it comes from the aspect map, not a single param — so it is not marked
    /// here. Default false: the workflow's ETA then falls back to a flat per-model average, exactly as before.</summary>
    public bool EtaVariable { get; init; }
}

/// <summary>The runtime context an <see cref="IWorkflow.Normalize"/> pass may need beyond the param bag. It's empty
/// at the ENQUEUE pass (params only — the frame-count snap needs nothing else, and it's what produces the user
/// notice before the placeholder renders) and populated at the SUBMIT pass (<see cref="AtSubmit"/> true, with the
/// source image dimensions + resolved requirements — the inputs the pixel-art resolution snap needs). A normalization
/// that needs submit-only context guards on <see cref="AtSubmit"/> so it no-ops at enqueue.</summary>
public sealed class NormalizeContext
{
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public ResolvedRequirements? Requirements { get; init; }
    /// <summary>True only on the submit-time pass (source dims + requirements available); false at enqueue.</summary>
    public bool AtSubmit { get; init; }
    /// <summary>The enqueue pass: params only, no source/requirements — frame snap fires, submit-only snaps skip.</summary>
    public static readonly NormalizeContext Empty = new();
}

/// <summary>A resolved, read-only bag of parameter values (workflow defaults overlaid by the configuration's
/// settings layer). Values may arrive as CLR primitives (workflow defaults) or <see cref="JsonElement"/>
/// (parsed from workflows.json); every accessor coerces both forms.</summary>
public sealed class ParamValues
{
    private readonly IReadOnlyDictionary<string, object?> _v;
    public ParamValues(IReadOnlyDictionary<string, object?> v) => _v = v;

    public bool Has(string key) => _v.ContainsKey(key) && _v[key] is not null;
    public object? Raw(string key) => _v.TryGetValue(key, out var v) ? v : null;

    public int Int(string key, int dflt = 0) => (int)Math.Round(Dbl(key, dflt));

    /// <summary>Coerce a raw param to a 64-bit int (seeds need the full long range, which <see cref="Int"/> truncates).</summary>
    public long Long(string key, long dflt = 0)
    {
        var v = Raw(key);
        return v switch
        {
            null => dflt,
            JsonElement je => je.ValueKind == JsonValueKind.Number
                ? (je.TryGetInt64(out var l) ? l : (je.TryGetDouble(out var d) ? (long)d : dflt)) : dflt,
            long l => l,
            int i => i,
            double d => (long)d,
            float f => (long)f,
            string s => long.TryParse(s, out var p) ? p : dflt,
            _ => dflt
        };
    }

    /// <summary>Coerce a raw param value (a CLR primitive or a parsed <see cref="JsonElement"/>) to an int — the
    /// static sibling of <see cref="Int"/>, for callers (e.g. <see cref="IWorkflow.Normalize"/>) holding a loose
    /// value out of the merged param bag rather than a key.</summary>
    public static int AsInt(object? v, int dflt = 0) => v switch
    {
        null => dflt,
        JsonElement je => je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var d) ? (int)Math.Round(d) : dflt,
        double d => (int)Math.Round(d),
        float f => (int)Math.Round(f),
        long l => (int)l,
        int i => i,
        string s => int.TryParse(s, out var p) ? p : dflt,
        _ => dflt
    };

    public double Dbl(string key, double dflt = 0)
    {
        var v = Raw(key);
        return v switch
        {
            null => dflt,
            JsonElement je => je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var d) ? d : dflt,
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            string s => double.TryParse(s, out var p) ? p : dflt,
            _ => dflt
        };
    }

    public string? Str(string key)
    {
        var v = Raw(key);
        return v switch
        {
            null => null,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString()
                            : je.ValueKind is JsonValueKind.Null ? null : je.ToString(),
            string s => s,
            _ => v.ToString()
        };
    }

    /// <summary>A model-ref parameter the graph cannot be built without — the resolved FILENAME, or a failure naming
    /// the parameter.
    ///
    /// <para>Exists because the alternative was written six times: <c>p.Str("motion_model") ?? "v3_sd15_mm.ckpt"</c>.
    /// A hardcoded stand-in makes an unbound or missing slot render perfectly on the one machine that happens to
    /// have that file, and on nobody else's — which is how a configuration whose slot had been deleted outright kept
    /// reporting success. If a graph genuinely cannot proceed without a model, say so; do not guess its name.</para></summary>
    public string Model(string key) =>
        Str(key) is { } s && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new RenderValidationException(
                $"This configuration needs a model for '{key}' and none is set. The configuration should name a slot "
                + "there, and this machine should have a file bound to it.");

    public bool Bool(string key, bool dflt = false)
    {
        var v = Raw(key);
        return v switch
        {
            null => dflt,
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => dflt
            },
            bool b => b,
            _ => dflt
        };
    }

    /// <summary>Nullable double: returns null when the key is absent/null (distinct from a 0 default). Used for
    /// optional knobs like FluxGuidance / ModelSamplingAuraFlow where "unset" means "omit the node".</summary>
    public double? DblOrNull(string key)
    {
        var v = Raw(key);
        return v switch
        {
            null => null,
            JsonElement je => je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var d) ? d : (double?)null,
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            _ => null
        };
    }

    /// <summary>A string array param (e.g. Qwen reference input slots ["image2","image3"]); empty when absent.</summary>
    public string[] StrArray(string key)
    {
        var v = Raw(key);
        if (v is JsonElement je && je.ValueKind == JsonValueKind.Array)
            return je.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
        if (v is string[] arr) return arr;
        if (v is IEnumerable<string> en) return en.ToArray();
        return Array.Empty<string>();
    }

    /// <summary>An [w,h] pair from an aspect map param (e.g. <c>aspect</c> = { square:[1024,1024], landscape:[..],
    /// portrait:[..] }). Falls back to the flat <c>width</c>/<c>height</c> params when the sub-key is absent.</summary>
    public (int w, int h) Dims(string aspectKey, string sub, int fallbackW, int fallbackH)
    {
        if (Raw(aspectKey) is JsonElement je && je.ValueKind == JsonValueKind.Object
            && je.TryGetProperty(sub, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() >= 2
            && arr[0].ValueKind == JsonValueKind.Number && arr[1].ValueKind == JsonValueKind.Number)
            return (arr[0].GetInt32(), arr[1].GetInt32());
        return (fallbackW, fallbackH);
    }
}

/// <summary>Runtime data a workflow build consumes that is NOT a stored parameter: the finalized prompt text,
/// the chosen aspect, and (for edits) the already-uploaded ComfyUI input-folder filenames of the source and any
/// reference images. Replaces the loose argument lists the old <c>BuildWorkflow</c>/<c>BuildEditWorkflow</c> took.</summary>
public sealed class WorkflowInputs
{
    public string Positive { get; init; } = "";
    public string? Negative { get; init; }
    public string Aspect { get; init; } = "square";
    public string? SourceImageName { get; init; }
    /// <summary>Video-to-video only: the uploaded source CLIP's ComfyUI input-folder filename (an mp4/webm — an
    /// animated-webp source is transcoded to mp4 before upload), loaded with <c>LoadVideo</c>. Null for image edits.</summary>
    public string? SourceVideoName { get; init; }
    /// <summary>Pixel dimensions of the source image (0 if unknown). The render-resolution snap reads these so it can
    /// derive the target aspect from the source without a UI width/height field.</summary>
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public IReadOnlyList<string> ReferenceImageNames { get; init; } = Array.Empty<string>();
    /// <summary>Inpaint only: the uploaded white-on-black mask's ComfyUI filename (the region to regenerate).</summary>
    public string? MaskImageName { get; init; }
    /// <summary>Image→video first/last-frame conditioning only: the uploaded ComfyUI filename of the LAST frame the
    /// clip should end on (the source image is the first frame). Null = animate freely from the first frame. Consumed
    /// by the WAN i2v workflow (which swaps to <c>WanFirstLastFrameToVideo</c> when set); ignored by workflows that
    /// don't support an end frame.</summary>
    public string? EndImageName { get; init; }
    /// <summary>The user's LoRA stack for THIS generation (empty for none): each a subfolder-qualified <c>lora_name</c>
    /// + strength, chained through <c>LoraLoader</c> (model + CLIP) on top of any preset LoRA. Consumed by
    /// <c>Txt2ImgWorkflowBase.Build</c>; edit workflows ignore it.</summary>
    public IReadOnlyList<LoraSelection> Loras { get; init; } = Array.Empty<LoraSelection>();
}

/// <summary>The concrete on-disk filenames a workflow loads, resolved from a configuration's requirement-id links
/// through the requirement registry. <c>Checkpoint</c> is the ckpt_name/unet_name; the rest are optional per shape.</summary>
public sealed class ResolvedRequirements
{
    public string Checkpoint { get; init; } = "";
    public IReadOnlyList<string> TextEncoders { get; init; } = Array.Empty<string>();
    public string? Vae { get; init; }
    public string? MotionModel { get; init; }
    public string? ControlNet { get; init; }
    /// <summary>The checkpoint model's documented resolution envelope (null if the model has no resolution block),
    /// for snapping the render size onto a clean grid multiple.</summary>
    public ModelResolution? Resolution { get; init; }
}

/// <summary>A model's documented supported output-resolution envelope (side bounds + the latent step its render
/// dimensions must be a multiple of). Stored per requirement in requirements.json. <c>MinW</c>/<c>MinH</c> and
/// <c>MaxW</c>/<c>MaxH</c> are the smallest/largest side; asymmetric handling is left to the caller.</summary>
public sealed class ModelResolution
{
    public int MinW { get; init; }
    public int MinH { get; init; }
    public int MaxW { get; init; }
    public int MaxH { get; init; }
    public int Step { get; init; } = 16;
}

/// <summary>The kind of a requirement file — determines its default target folder and which loader consumes it.</summary>
/// <summary>
/// Which loader's file list a slot draws from. Each value maps to ONE loader input, so a kind names exactly the
/// set of files that can fill it.
///
/// <para>There is deliberately no catch-all. A single <c>Other</c> value used to absorb loras, IP-adapters,
/// CLIP-vision models, latent upsamplers and SeedVR2 weights into one pool, which meant every slot
/// of any of those types was offered every file of all of them — 27 unrelated files on one box, and a LoRA was a
/// selectable answer for a ControlNet pack. An unrecognised kind now fails to load rather than quietly joining a
/// bucket.</para>
/// </summary>
public enum RequirementKind
{
    Checkpoint, Unet, UnetGguf, Vae, TextEncoder, MotionModel, ControlNet, UpscaleModel,
    Lora, ClipVision, IpAdapter, LatentUpscaleModel, SeedVr2,

    /// <summary>Met by ComfyUI having a node registered, not by a file, so it draws from no loader at all.</summary>
    CustomNode,
}

/// <summary>
/// A bindable slot: one model file a configuration needs, from <c>configurations/models/&lt;id&gt;.json</c>.
///
/// <para>Deliberately carries no filename. Which file fills a slot is a fact about a machine's disk, not about
/// the model, and lives in <c>dbo.ModelBinding</c> where a user can correct it. Shipping one filename per slot
/// is what made a workflow vanish silently for anyone whose copy was named differently.</para>
/// </summary>
public sealed class Requirement
{
    public required string Id { get; init; }

    /// <summary>Which loader's file list this slot draws from. Binding and matching never cross kinds.</summary>
    public required RequirementKind Kind { get; init; }

    /// <summary>Human name for the binding UI, e.g. "Pony Diffusion V6 XL".</summary>
    public string Label { get; init; } = "";

    /// <summary>
    /// Published-name patterns used to recognise this model among the files ComfyUI reports, or empty. Empty is
    /// the common case: most slots are bound by hand, and a wrong guess is worse than no guess.
    /// </summary>
    public IReadOnlyList<string> Match { get; init; } = Array.Empty<string>();

    /// <summary>
    /// A node type whose presence in <c>object_info</c> IS this requirement being met — for a custom-node pack
    /// rather than a file.
    ///
    /// <para>Most packs are gated for free: their loaders are where their filenames come from, so an uninstalled
    /// pack takes its files with it. A pack whose node loads nothing has no filenames to disappear —
    /// <c>AnimaLLLiteApply</c> patches a model it is handed — so nothing about it can be inferred from a file
    /// list, and a workflow needing it read as ready right up until submit failed on an unregistered node. Naming
    /// the node is how such a requirement becomes checkable at all.</para>
    ///
    /// <para>There is nothing to bind: it is present or it is not, so <see cref="Match"/> and the binding UI do
    /// not apply.</para>
    /// </summary>
    public string? Node { get; init; }
}

/// <summary>A configuration's soft links to its requirements, by requirement id. Resolved to filenames via the
/// requirement registry at submit time, and used (as the union of ids) for presence-gating the API list.</summary>
public sealed class RequirementLinks
{
    public string Checkpoint { get; init; } = "";
    public IReadOnlyList<string> TextEncoders { get; init; } = Array.Empty<string>();
    public string? Vae { get; init; }
    public string? MotionModel { get; init; }
    public string? ControlNet { get; init; }
    /// <summary>Any other linked requirements that don't fit the named slots (e.g. IP-Adapter, CLIP-vision, a LoRA),
    /// for presence-gating. Not consumed by the graph build directly — the workflow reads filenames from params —
    /// they exist so the config is hidden when a dependency file is missing.</summary>
    public IReadOnlyList<string> Extra { get; init; } = Array.Empty<string>();

    /// <summary>Every linked requirement id (non-empty), for presence-gating.</summary>
    public IEnumerable<string> All()
    {
        if (!string.IsNullOrEmpty(Checkpoint)) yield return Checkpoint;
        foreach (var te in TextEncoders) if (!string.IsNullOrEmpty(te)) yield return te;
        if (!string.IsNullOrEmpty(Vae)) yield return Vae!;
        if (!string.IsNullOrEmpty(MotionModel)) yield return MotionModel!;
        if (!string.IsNullOrEmpty(ControlNet)) yield return ControlNet!;
        foreach (var x in Extra) if (!string.IsNullOrEmpty(x)) yield return x;
    }
}

/// <summary>One key of a configuration's settings layer: the value supplied for a workflow parameter, plus whether
/// it is surfaced to the UI as an editable control (vs a retained, hidden default).</summary>
public sealed class ConfigParam
{
    public required object? Value { get; init; }
    public bool Exposed { get; init; }
    /// <summary>A knob explicitly declared object-form with <c>"exposed": false</c> — a baked-in value that is neither
    /// surfaced to the UI nor overridable by a caller's request (the value is enforced on every generation). Bare
    /// scalar defaults (e.g. <c>"steps": 8</c>) are NOT locked and remain freely overridable via the request.</summary>
    public bool Locked { get; init; }
    /// <summary>Optional per-config range override for an exposed numeric control.</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>Optional per-config UI increment override for an exposed numeric control (falls back to the schema's Step).</summary>
    public double? Step { get; init; }
}

/// <summary>
/// A workflow configuration — a row of workflows.json and the unit the API exposes. It binds one
/// <see cref="IWorkflow"/> (<see cref="WorkflowName"/>), supplies its settings layer (<see cref="Params"/>),
/// soft-links its requirements, and carries the decision-card/prompting metadata. <see cref="Id"/> is unique
/// (the binding key the client sends as <c>model</c>); <see cref="FriendlyName"/> MAY be shared across configs
/// (the shared-display-name case) — paired with disjoint VRAM bands so exactly one is eligible per machine.
/// </summary>
public sealed class WorkflowConfiguration
{
    public required string Id { get; init; }
    public required string WorkflowName { get; init; }
    public string? FriendlyName { get; init; }
    public IReadOnlyDictionary<string, ConfigParam> Params { get; init; } = new Dictionary<string, ConfigParam>();
    public RequirementLinks Requirements { get; init; } = new();
    /// <summary>For edit-kind configs: an optional effect category (e.g. "Line art", "Pixelize"). When set, the editor
    /// UI files this config under the Effects tab and groups the dropdown by this value; null = a plain editor.</summary>
    public string? EffectType { get; init; }
    /// <summary>For edit-kind configs that are NOT effects: an optional section label for the editor's model picker
    /// (e.g. "Redraw"). Unlike <see cref="EffectType"/> it does not move the config to the Effects tab — it only
    /// segregates it under a header within the Edit tab. Null = an ungrouped plain editor, listed above every header.</summary>
    public string? EditGroup { get; init; }
    public bool Default { get; init; }
    public ModelCard Card { get; init; } = new();

    /// <summary>
    /// The output-resolution envelope PixelSnap clamps render size to, or null to fall back to the workflow's own
    /// <c>ResolutionEnvelope</c>. It used to hang off the checkpoint's requirement; it lives on the configuration
    /// now that a model file carries identity only. Overriding it is a ConfigOverride, not an edit to the file.
    /// </summary>
    public ModelResolution? Resolution { get; init; }
}
