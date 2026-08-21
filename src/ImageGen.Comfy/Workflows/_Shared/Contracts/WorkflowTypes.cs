using ImageGen.Application.Rendering;
using ImageGen.Application.Workflows;
using ImageGen.Comfy.Edit.HunyuanVideo15I2V;
using ImageGen.Comfy.Generation.HunyuanVideo15T2V;
using ImageGen.Domain;
using ImageGen.Domain.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageGen.Comfy;

/// <summary>
/// The runtime kind of a workflow. <see cref="Generate"/> (text→image/video) is offered to /generate; every other
/// value is an editor offered to /edit — an editor is exactly "not <see cref="Generate"/>".
///
/// <para>Classes declare their kind, but only the four they can know from the class alone: <see cref="Generate"/>,
/// <see cref="Edit"/> (the residual instruction editor), and the dedicated <see cref="Inpaint"/>/<see cref="Outpaint"/>
/// classes. The remaining values are CONFIGURATION-level — <see cref="Redraw"/>/<see cref="Upscale"/> from the
/// config's edit-group, <see cref="Effect"/> from its effect-type, and <see cref="Animate"/>/<see cref="VideoEdit"/>
/// from the media — so a class always reports one of the first four and the catalog RESOLVES the specific kind per
/// configuration (see <c>WorkflowCatalogService.ResolveKind</c>). That resolved kind is the single authoritative value
/// the catalog API emits per config, so the workflows-page badge and the edit-page tab routing both read one declared
/// field instead of re-deriving it from names, magic strings, or side-channels (issue #163).</para>
/// </summary>
public enum WorkflowKind
{
    /// <summary>Text→image/video generation (offered to /generate).</summary>
    Generate,

    /// <summary>Instruction editing (add/remove/replace a change) — the residual "Edit" tab.</summary>
    Edit,

    /// <summary>Masked-region regeneration.</summary>
    Inpaint,

    /// <summary>Canvas extension / border fill.</summary>
    Outpaint,

    /// <summary>Whole-image img2img redraw (no mask, no instruction). Resolved from the config's edit-group.</summary>
    Redraw,

    /// <summary>Feed-forward super-resolution / SeedVR2. Resolved from the config's edit-group.</summary>
    Upscale,

    /// <summary>Effect editors (Line art, Pixelize, …). Resolved from the config's effect-type.</summary>
    Effect,

    /// <summary>Image→video. Resolved from the media (still source, video output).</summary>
    Animate,

    /// <summary>Video→video (the clip-source pixel-quantize "Pixelize" pass). Resolved from a video source media.</summary>
    VideoEdit,
}

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
public enum ParamType { Int, Double, String, Multiline, Bool, Enum }

/// <summary>How a configuration's diffusion model is loaded — the closed vocabulary of the <c>loader</c> param.
/// <see cref="Checkpoint"/> is an all-in-one checkpoint (model+CLIP+VAE) via <c>CheckpointLoaderSimple</c>;
/// <see cref="Unet"/> / <see cref="UnetGguf"/> are a diffusion-only UNet (safetensors / GGUF) with CLIP and VAE loaded
/// separately. Only checkpoint-vs-split changes the graph head — UNet-vs-GGUF is decided downstream from the file
/// extension (<see cref="ComfyGraph.DiffusionLoaderNode"/>) — but all three stay distinct config-facing choices so a
/// configuration declares its intent.</summary>
public enum LoaderKind { Checkpoint, Unet, UnetGguf }

/// <summary>The single source of truth for the <c>loader</c> param: its key, its wire vocabulary (what a configuration
/// writes and the schema offers), and the parse from wire string to <see cref="LoaderKind"/> — replacing the literal
/// <c>"loader"</c> key and the <c>{ "checkpoint", "unet", "unet_gguf" }</c> choice array that were re-typed across the
/// schemas and every access site.</summary>
internal static class LoaderKinds
{
    /// <summary>The param key a workflow reads the loader kind from.</summary>
    public const string ParamKey = "loader";

    public const string Checkpoint = "checkpoint";
    public const string Unet = "unet";
    public const string UnetGguf = "unet_gguf";
}

/// <summary>Wire ⇄ <see cref="LoaderKind"/> conversion for the <see cref="LoaderKinds"/> vocabulary: the schema's
/// choice list and the parse from wire string — kept apart from the const holder so the holder stays pure.</summary>
internal static class LoaderKindWire
{
    /// <summary>The wire vocabulary in dropdown order, for a schema's <see cref="ParamSpec.Choices"/>.</summary>
    public static readonly string[] Choices = [LoaderKinds.Checkpoint, LoaderKinds.Unet, LoaderKinds.UnetGguf];

    /// <summary>Wire string → kind, or a refusal naming the value — a loader outside the closed set is a broken
    /// configuration, not a silent fall-through to the split-loader branch.</summary>
    public static LoaderKind Parse(string wire) => wire switch
    {
        LoaderKinds.Checkpoint => LoaderKind.Checkpoint,
        LoaderKinds.Unet => LoaderKind.Unet,
        LoaderKinds.UnetGguf => LoaderKind.UnetGguf,
        _ => throw new RenderValidationException(
            $"Unknown loader '{wire}'. A configuration's loader must be one of: {string.Join(LoaderWireText.ChoiceSeparator, Choices)}."),
    };
}

/// <summary>List separator for naming the accepted loader values in the parse refusal message.</summary>
file static class LoaderWireText
{
    public const string ChoiceSeparator = ", ";
}

/// <summary>A stepped frame-count rule for video models: the only valid clip lengths are <c>Base + k*Step</c>
/// (k ≥ 0) — LTX = (1, 8) → 1, 9, 17, …, 97; Wan = (1, 4) → 1, 5, 9, …. It's a property of the model's VAE temporal
/// compression, mirrored from the underlying ComfyUI node's <c>length</c> step. Null on a workflow means no frame
/// constraint (stills / unconstrained models). Read at enqueue (see <see cref="IWorkflow.Normalize"/>) to snap an
/// out-of-range requested length onto the nearest valid value and surface a notice — so a bad value neither crashes
/// the model nor hard-rejects the job (which would block a mixed-model batch where each model has its own rule).</summary>
public sealed record FrameRule(int Base, int Step)
{
    /// <summary>Whether <paramref name="n"/> is a positive member of this cadence.</summary>
    public bool IsValid(int n) => n > 0 && Step > 0 && n >= Base && (n - Base) % Step == 0;

    /// <summary>The smallest valid length that is &gt;= <paramref name="n"/> — i.e. round UP to the next
    /// <see cref="Base"/> + k*<see cref="Step"/>. Always rounding up (never down) means the snap never renders FEWER
    /// frames than asked, and is consistent across step sizes (30 → 33 for both an 8n+1 and a 4n+1 model). Already-valid
    /// values are returned unchanged.</summary>
    public int Snap(int n)
    {
        if (Step <= 0 || n <= Base)
        {
            return Base >= n ? Base : n;
        }

        int k = (int)Math.Ceiling((n - Base) / (double)Step);
        return Base + (k * Step);
    }

    /// <summary>The NEAREST valid length to <paramref name="n"/> — round to the closest <see cref="Base"/> + k*<see
    /// cref="Step"/> (ties → up). Unlike <see cref="Snap"/>, which only ever rounds up, this is for a value the user
    /// chose in a friendlier unit (seconds) where the honest answer is the closest real length the model can render,
    /// not the next one up. Values at or below <see cref="Base"/> return <see cref="Base"/>.</summary>
    public int SnapNearest(int n)
    {
        if (Step <= 0)
        {
            return n;
        }

        if (n <= Base)
        {
            return Base;
        }

        int k = (int)Math.Round((n - Base) / (double)Step, MidpointRounding.AwayFromZero);
        return Base + (k * Step);
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
    [AllowNullable("null = the control declares no minimum bound; 0 is a real minimum, distinct from unbounded")]
    public double? Min { get; init; }
    [AllowNullable("null = the control declares no maximum bound; 0 is a real maximum, distinct from unbounded")]
    public double? Max { get; init; }
    /// <summary>UI numeric-input increment for an exposed int/double control. Null → the frontend's per-type default
    /// (1 for int, 0.1 for double). Set it to match the value's precision so the default is reachable (e.g. a 0.35
    /// default on a 0.1 step is not — it needs 0.01).</summary>
    [AllowNullable("null = no explicit step, so the frontend uses its per-type default (1 for int, 0.1 for double); distinct from a 0 step")]
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
    /// <para>Holding filenames directly would put a second set of the author's filenames in the configurations,
    /// outside the binding system and unchangeable by a user. Declaring the reference is deliberate rather than
    /// inferring it from the value: a rule like "resolve anything that looks like a slot id" silently rewrites any
    /// parameter whose value happens to collide.</para>
    /// </summary>
    public bool IsModelRef { get; init; }

    /// <summary>True when this parameter's value materially drives render TIME, so its merged value is captured with
    /// each timing sample and used to param-match the ETA (steps, frame count). Resolution is a time driver too but is
    /// captured from the RESOLVED render (w,h) — it comes from the aspect map, not a single param — so it is not marked
    /// here. Default false: the workflow's ETA then falls back to a flat per-model average.</summary>
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
    /// <summary>True when this video workflow explicitly passes positive frame counts through without cadence snap.</summary>
    public bool AllowUntrainedFrameCounts { get; init; }
    /// <summary>True only on the submit-time pass (source dims + requirements available); false at enqueue.</summary>
    public bool AtSubmit { get; init; }
    /// <summary>The enqueue pass: params only, no source/requirements — frame snap fires, submit-only snaps skip.</summary>
    public static readonly NormalizeContext Empty = new();
}

/// <summary>The one place the merged parameter bag (workflow defaults overlaid by the configuration and request
/// layers, values as CLR primitives or <see cref="JsonElement"/>) is turned into a strongly-typed DTO. Every
/// submission crosses this boundary exactly once — <see cref="IWorkflow.Build"/> reads its own params DTO here, and
/// the client reads <see cref="SubmissionCommon"/> here — and stays typed from that point to the wire.</summary>
[AllowMagicStrings("human-readable numeric validation descriptions")]
public static class ParamsCodec
{
    /// <summary>System.Text.Json settings for reading the bag into a typed params DTO: the DTO's own contract enforces
    /// itself (<c>RespectRequiredConstructorParameters</c> + <c>RespectNullableAnnotations</c>, per #103), so a
    /// <c>required</c> / non-nullable member throws on an absent or null value at the deserializer rather than via a
    /// hand-written guard. Unmapped keys are ignored by default.</summary>
    private static readonly JsonSerializerOptions ParamsJsonOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        // A params DTO may be a CONTRACT hierarchy ([JsonPolymorphic] on a discriminator key like "engine"): the merged
        // bag is a dictionary, so the discriminator is not guaranteed to be the first property. Read it wherever it sits.
        AllowOutOfOrderMetadataProperties = true,
        // Contract hierarchies whose discriminator is a BOOL (the HunyuanVideo 1.5 `sr` toggle) can't be expressed with
        // [JsonPolymorphic] (string/int discriminators only), so a converter reads the toggle and materializes the
        // matching SR-or-not shape. Registered against the abstract bases only — the concrete subtypes deserialize normally.
        Converters =
        {
            new HunyuanVideo15I2VParamsConverter(),
            new HunyuanVideo15T2VParamsConverter(),
        },
    };

    /// <summary>Deserialize the merged parameters into a strongly-typed params DTO in ONE System.Text.Json pass — STJ
    /// does the <see cref="JsonElement"/>→typed coercion and honours the DTO's <c>[JsonPropertyName]</c>s / <c>required</c>
    /// members, so a workflow never touches a string key or a loose accessor — then enforce the DTO's declared value
    /// bounds (<see cref="ValidateBounds"/>) before the typed object is handed back. A value outside a declared
    /// <c>[Range]</c> (steps, cfg, …) is refused HERE, at the single typed boundary every submission crosses, rather
    /// than reaching the graph — so a <c>steps: 5000</c> sent past the UI slider fails fast, naming the value and its
    /// bound. <paramref name="ignoredBoundKey"/> is the narrow workflow-policy seam for a single explicitly opted-out
    /// wire field; required members, coercion, and every other bound remain enforced.</summary>
    public static T Deserialize<T>(
        IReadOnlyDictionary<string, object?> bag,
        string? ignoredBoundKey = null)
    {
        T dto = JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToElement(bag), ParamsJsonOptions)
            ?? throw new RenderValidationException($"The merged parameters could not be read as {typeof(T).Name}.");
        ValidateBounds(dto, ignoredBoundKey);
        return dto;
    }

    /// <summary>Enforce the DataAnnotations bounds declared on a params DTO's members (<c>[Range]</c> and friends),
    /// reflectively — the enforcement half of the "declare the bound once, on the typed model" design: the bound lives
    /// as an attribute next to the property it constrains, and this runs it. A nullable member with no supplied value
    /// is skipped (an absent optional param is "unspecified", not out of range). Every violation is collected and
    /// reported together, each naming the wire key, the offending value, and the permitted range.</summary>
    [AllowMagicStrings("human-readable out-of-range parameter refusal message")]
    private static void ValidateBounds(object dto, string? ignoredBoundKey)
    {
        List<ValidationResult> results = [];
        if (Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true))
        {
            return;
        }

        Type t = dto.GetType();
        List<string> problems = [];
        foreach (ValidationResult r in results)
        {
            IEnumerable<string> members = r.MemberNames.Any() ? r.MemberNames : [string.Empty];
            foreach (string member in members)
            {
                PropertyInfo? prop = member.Length > 0 ? t.GetProperty(member) : null;
                string key = prop?.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? (member.Length > 0 ? member : t.Name);
                if (string.Equals(key, ignoredBoundKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                RangeAttribute? range = prop?.GetCustomAttribute<RangeAttribute>();
                if (prop is not null && range is not null)
                {
                    problems.Add($"'{key}' must be between {range.Minimum} and {range.Maximum}, but was {prop.GetValue(dto)}");
                }
                else
                {
                    problems.Add($"'{key}': {r.ErrorMessage}");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new RenderValidationException(
                $"This request has out-of-range parameter value(s): {string.Join("; ", problems)}.");
        }
    }

    /// <summary>Coerce a raw param value (a CLR primitive or a parsed <see cref="JsonElement"/>) to an int — for the
    /// pre-DTO normalization pass (<see cref="IWorkflow.Normalize"/>), which mutates the loose bag BEFORE it is
    /// deserialized and so holds a value, not a typed member.</summary>
    public static int AsInt(object? v, int dflt = 0) => v switch
    {
        null => dflt,
        JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out int i) => i,
        JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out double d) => CheckedInt(d),
        JsonElement je => throw NumericValueException(je.ToString(), "an integer"),
        double d => CheckedInt(d),
        float f => CheckedInt(f),
        decimal m => CheckedInt((double)m),
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        long l => throw NumericValueException(l, "a 32-bit integer"),
        int i => i,
        string s when int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int p) => p,
        string s => throw NumericValueException(s, "an integer"),
        _ => throw NumericValueException(v, "an integer"),
    };

    /// <summary>Read a bag value as a double, tolerating the same loose forms as <see cref="AsInt"/> — used by the
    /// pre-DTO normalization pass (video length in seconds, fps) that runs on the loose bag before it is typed.</summary>
    public static double AsDouble(object? v, double dflt = 0) => v switch
    {
        null => dflt,
        JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out double d) => CheckedDouble(d),
        JsonElement je => throw NumericValueException(je.ToString(), "a finite number"),
        double d => CheckedDouble(d),
        float f => CheckedDouble(f),
        decimal m => CheckedDouble((double)m),
        long l => l,
        int i => i,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double p) => CheckedDouble(p),
        string s => throw NumericValueException(s, "a finite number"),
        _ => throw NumericValueException(v, "a finite number"),
    };

    private static int CheckedInt(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue || value != Math.Truncate(value))
        {
            throw NumericValueException(value, "an integer");
        }

        return (int)value;
    }

    private static double CheckedDouble(double value) => double.IsFinite(value)
        ? value
        : throw NumericValueException(value, "a finite number");

    private static RenderValidationException NumericValueException(object? value, string expected) =>
        new($"Parameter value '{value}' must be {expected}; it cannot be silently replaced with a default.");
}

/// <summary>The cross-workflow submission parameters the client (not a workflow) reads off the merged bag: the ETA
/// render-size + time drivers, and the generation prompt rules (required tag prefix, model negative, distilled-model
/// negative suppression). Deserialized once via <see cref="ParamsCodec"/> so the client's ETA/prompt logic runs on
/// typed values instead of loose accessors — the same keys a workflow's own DTO also reads for the graph.</summary>
public sealed record SubmissionCommon
{
    [JsonPropertyName(WorkflowParamKeys.Steps)]
    [Range(ParamBounds.StepsMin, ParamBounds.StepsMax)]
    [AllowNullable("null = the config didn't set steps in the merged bag; the client reads the value only when present, distinct from a real 0")] public int? Steps { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Length)]
    [AllowNullable("null = the config didn't set a clip length; distinct from a real 0-frame length")] public int? Length { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Width)] public int Width { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Height)] public int Height { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Aspect)] public Dictionary<string, int[]>? Aspect { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Megapixels)]
    [AllowNullable("null = the config exposes no megapixels control; the ETA/guard size is the aspect-map/flat-W/H size unchanged, distinct from a real 0 budget")] public double? Megapixels { get; init; }
    [JsonPropertyName(WorkflowParamKeys.RequiredPrefix)] public string? RequiredPrefix { get; init; }
    [JsonPropertyName(WorkflowParamKeys.PromptTemplate)] public string? PromptTemplate { get; init; }
    [JsonPropertyName(WorkflowParamKeys.Cfg)]
    [Range(ParamBounds.CfgMin, ParamBounds.CfgMax)]
    [AllowNullable("null = the config didn't set CFG (a custom-build model supplies its own guidance); 0 is a real CFG value")] public double? Cfg { get; init; }
    [JsonPropertyName(WorkflowParamKeys.NegativeSupported)] public bool NegativeSupported { get; init; } = true;
    [JsonPropertyName(WorkflowParamKeys.Negative)] public string? Negative { get; init; }
    [JsonPropertyName(WorkflowParamKeys.SnapResolution)] public bool SnapResolution { get; init; }

    /// <summary>The ETA render size: the aspect map's <paramref name="sub"/> entry, else the flat width/height (0,0
    /// when neither is set — the ETA falls back to the model average). Mirrors the size a workflow's Build lays out.</summary>
    public (int w, int h) Dims(string sub) =>
        Aspect is not null && Aspect.TryGetValue(sub, out int[]? wh) && wh.Length >= 2 ? (wh[0], wh[1]) : (Width, Height);
}

/// <summary>Runtime data a workflow build consumes that is NOT a stored parameter: the finalized prompt text,
/// the chosen aspect, and (for edits) the already-uploaded ComfyUI input-folder filenames of the source and any
/// reference images.</summary>
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
    /// <summary>The selected per-configuration edit-quality working budget, in megapixels.</summary>
    [AllowNullable("null = this workflow does not support the edit-quality MP selector")]
    public double? EditMegapixels { get; init; }
    /// <summary>The uploaded reference inputs, in order, EACH carrying its media kind (image/audio/video) so a workflow
    /// routes it to the correct graph input. Most editors take only images — <see cref="ImageReferences"/> is the
    /// convenience for them; a multi-modal workflow (e.g. one taking a driving audio/video reference) reads this and
    /// filters by <see cref="ReferenceInput.Kind"/>.</summary>
    public IReadOnlyList<ReferenceInput> References { get; init; } = [];

    /// <summary>The uploaded ComfyUI filenames of the IMAGE references only, in order — the common case for edit
    /// workflows that condition on reference stills. Non-image references (if any) are omitted.</summary>
    public IReadOnlyList<string> ImageReferences => [.. References.Where(r => r.Kind == ReferenceKind.Image).Select(r => r.Name)];
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
    public IReadOnlyList<LoraSelection> Loras { get; init; } = [];
}

/// <summary>One uploaded reference: its ComfyUI input-folder filename and the media <see cref="Kind"/> the render path
/// derived from the stored blob's content type (never from the caller). Positional to the workflow that consumes it.</summary>
public sealed record ReferenceInput(string Name, ReferenceKind Kind);

/// <summary>The concrete on-disk filenames a workflow loads, resolved from a configuration's requirement-id links
/// through the requirement registry. <c>Checkpoint</c> is the ckpt_name/unet_name; the rest are optional per shape.</summary>
public sealed class ResolvedRequirements
{
    public string Checkpoint { get; init; } = "";
    public IReadOnlyList<string> TextEncoders { get; init; } = [];
    public string? Vae { get; init; }
    public string? MotionModel { get; init; }
    public string? ControlNet { get; init; }
    /// <summary>The checkpoint model's documented resolution envelope (null if the model has no resolution block),
    /// for snapping the render size onto a clean grid multiple.</summary>
    public ModelResolution? Resolution { get; init; }
    /// <summary>True when this image-generation workflow's machine setting explicitly permits positive dimensions
    /// outside <see cref="Resolution"/>. The envelope remains present for warnings; graph sizing skips its clamp.</summary>
    public bool AllowUntrainedResolution { get; init; }
    /// <summary>True when this video workflow deliberately passes a positive frame count through without applying the
    /// DTO's trained length bound. ComfyUI remains authoritative for its own node validation.</summary>
    public bool AllowUntrainedFrameCounts { get; init; }

    /// <summary>The resolved filename for text encoder <paramref name="index"/>, or a refusal naming it — a REQUIRED
    /// encoder slot fails loudly rather than loading an empty name that only "works" on a machine that happens to hold
    /// the right file.</summary>
    public string TextEncoder(int index) =>
        index >= 0 && index < TextEncoders.Count && !string.IsNullOrWhiteSpace(TextEncoders[index])
            ? TextEncoders[index]
            : throw new RenderValidationException(
                $"This configuration needs text encoder #{index + 1} and none is bound to that slot on this machine.");

    /// <summary>The resolved VAE filename, or a refusal — for a workflow that cannot decode without one. Never an
    /// empty stand-in.</summary>
    public string RequiredVae() =>
        !string.IsNullOrWhiteSpace(Vae)
            ? Vae
            : throw new RenderValidationException("This configuration needs a VAE and none is bound on this machine.");

    /// <summary>The resolved checkpoint/diffusion filename, or a refusal — no empty name that fails obscurely at the
    /// loader. Presence-gating normally keeps an unbound config off the menu; this refuses the render if one slips
    /// through.</summary>
    public string RequiredCheckpoint() =>
        !string.IsNullOrWhiteSpace(Checkpoint)
            ? Checkpoint
            : throw new RenderValidationException("This configuration needs a checkpoint/diffusion model and none is bound on this machine.");

    /// <summary>The resolved second-model filename from the MotionModel slot, or a refusal — for a workflow that
    /// needs it (AnimateDiff motion module; Ideogram/Krea2's unconditional/refiner UNet) and cannot proceed without.</summary>
    public string RequiredMotionModel() =>
        !string.IsNullOrWhiteSpace(MotionModel)
            ? MotionModel
            : throw new RenderValidationException("This configuration needs a second model (the motion_model slot) and none is bound on this machine.");

    /// <summary>The resolved ControlNet filename, or a refusal — for a workflow that needs one (line-art ControlNet,
    /// the outpaint LLLite) and cannot build its graph without it.</summary>
    public string RequiredControlNet() =>
        !string.IsNullOrWhiteSpace(ControlNet)
            ? ControlNet
            : throw new RenderValidationException("This configuration needs a ControlNet and none is bound on this machine.");
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
/// <para>There is deliberately no catch-all. A single <c>Other</c> value would pool loras, IP-adapters,
/// CLIP-vision models, latent upsamplers and SeedVR2 weights together, so every slot of any of those types would be
/// offered every file of all of them — a LoRA a selectable answer for a ControlNet pack. An unrecognised kind fails
/// to load rather than quietly joining a bucket.</para>
/// </summary>
public enum RequirementKind
{
    Checkpoint, Unet, UnetGguf, Vae, TextEncoder, MotionModel, ControlNet, UpscaleModel,
    Lora, ClipVision, IpAdapter, LatentUpscaleModel, SeedVr2, Diffusers,

    /// <summary>Met by ComfyUI having a node registered, not by a file, so it draws from no loader at all.</summary>
    CustomNode,
}

/// <summary>
/// A bindable slot: one model file a configuration needs, from <c>configurations/models/&lt;id&gt;.json</c>.
///
/// <para>Deliberately carries no filename. Which file fills a slot is a fact about a machine's disk, not about
/// the model, and lives in <c>dbo.ModelBinding</c> where a user can correct it. Shipping one filename per slot
/// would make a workflow vanish silently for anyone whose copy was named differently.</para>
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
    public IReadOnlyList<string> Match { get; init; } = [];

    /// <summary>
    /// A node type whose presence in <c>object_info</c> IS this requirement being met — for a custom-node pack
    /// rather than a file.
    ///
    /// <para>Most packs are gated for free: their loaders are where their filenames come from, so an uninstalled
    /// pack takes its files with it. A pack whose node loads nothing has no filenames to disappear —
    /// <c>AnimaLLLiteApply</c> patches a model it is handed — so nothing about it can be inferred from a file
    /// list, and a workflow needing it would read as ready right up until submit fails on an unregistered node. Naming
    /// the node is how such a requirement becomes checkable at all.</para>
    ///
    /// <para>There is nothing to bind: it is present or it is not, so <see cref="Match"/> and the binding UI do
    /// not apply.</para>
    /// </summary>
    public string? Node { get; init; }

    /// <summary>The model's landing page — where a person goes to download the file this slot needs (a Hugging Face
    /// repo page, a Civitai model page). A page, never a direct file URL.</summary>
    public string? Page { get; init; }
}

/// <summary>A configuration's soft links to its requirements, by requirement id. Resolved to filenames via the
/// requirement registry at submit time, and used (as the union of ids) for presence-gating the API list.</summary>
public sealed class RequirementLinks
{
    public string Checkpoint { get; init; } = "";
    public IReadOnlyList<string> TextEncoders { get; init; } = [];
    public string? Vae { get; init; }
    public string? MotionModel { get; init; }
    public string? ControlNet { get; init; }
    /// <summary>Any other linked requirements that don't fit the named slots (e.g. IP-Adapter, CLIP-vision, a LoRA),
    /// for presence-gating. Not consumed by the graph build directly — the workflow reads filenames from params —
    /// they exist so the config is hidden when a dependency file is missing.</summary>
    public IReadOnlyList<string> Extra { get; init; } = [];

    /// <summary>Every linked requirement id (non-empty), for presence-gating.</summary>
    public IEnumerable<string> All()
    {
        if (!string.IsNullOrEmpty(Checkpoint))
        {
            yield return Checkpoint;
        }

        foreach (string te in TextEncoders)
        {
            if (!string.IsNullOrEmpty(te))
            {
                yield return te;
            }
        }

        if (!string.IsNullOrEmpty(Vae))
        {
            yield return Vae;
        }

        if (!string.IsNullOrEmpty(MotionModel))
        {
            yield return MotionModel;
        }

        if (!string.IsNullOrEmpty(ControlNet))
        {
            yield return ControlNet;
        }

        foreach (string x in Extra)
        {
            if (!string.IsNullOrEmpty(x))
            {
                yield return x;
            }
        }
    }
}

/// <summary>One key of a configuration's settings layer: the value supplied for a workflow parameter, plus its
/// explicit surfacing state (shown by default / hidden but user-revealable / locked structural constant).</summary>
public sealed class ConfigParam
{
    public required object? Value { get; init; }

    /// <summary>The param's explicit state. <see cref="ParamVisibility.Locked"/> is the only state the submit path
    /// enforces (the caller's override is dropped); <see cref="ParamVisibility.Exposed"/> and
    /// <see cref="ParamVisibility.Hidden"/> differ only in default UI surfacing, which a user may flip per-account.</summary>
    public required ParamVisibility Visibility { get; init; }
    /// <summary>Optional per-config range override for an exposed numeric control.</summary>
    [AllowNullable("null = no per-config minimum override, so the schema's Min stands; distinct from a real 0 bound")]
    public double? Min { get; init; }
    [AllowNullable("null = no per-config maximum override, so the schema's Max stands; distinct from a real 0 bound")]
    public double? Max { get; init; }
    /// <summary>Optional per-config UI increment override for an exposed numeric control (falls back to the schema's Step).</summary>
    [AllowNullable("null = no per-config step override, so the schema's Step stands; distinct from a real 0 step")]
    public double? Step { get; init; }
    /// <summary>Optional alternate UI range which must be explicitly enabled by the user. It changes numeric range
    /// validation only; type validation and model-specific normalization rules remain in force.</summary>
    public ConfigParamRangeOverride? RangeOverride { get; init; }
}

/// <summary>Configuration-side form of an explicitly enabled alternate numeric range.</summary>
public sealed record ConfigParamRangeOverride(
    [property: AllowNullable("null = the alternate range keeps the field's normal minimum")] double? Min,
    [property: AllowNullable("null = the alternate range keeps the field's normal maximum")] double? Max,
    string Label,
    string Warning);

/// <summary>
/// A workflow configuration — a row of workflows.json and the unit the API exposes. It binds one
/// <see cref="IWorkflow"/> (<see cref="WorkflowName"/>), supplies its settings layer (<see cref="Params"/>),
/// soft-links its requirements, and carries the decision-card/prompting metadata. <see cref="Id"/> is unique
/// (the binding key the client sends as <c>model</c>); <see cref="FriendlyName"/> MAY be shared across configs,
/// in which case the first requirement-eligible configuration in catalogue order supplies the displayed row.
/// </summary>
public sealed class WorkflowConfiguration
{
    public required string Id { get; init; }
    public required string WorkflowName { get; init; }
    public string? FriendlyName { get; init; }
    /// <summary>Optional compact picker label supplied by the catalog; clients fall back to FriendlyName.</summary>
    public string? ShortName { get; init; }
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

    /// <summary>The id of this Edit config's masked sibling — the Inpaint workflow submit routes to when a mask is
    /// drawn — or "" when it has none. A cross-config link is inherently optional; the empty string is the explicit
    /// "no sibling" value (not a nullable branch field). Validated at catalog projection: the target must exist, be
    /// Kind=Inpaint, and preserve composition, and only a plain Edit config may declare one.</summary>
    public string MaskWorkflow { get; init; } = "";
    public ModelCard Card { get; init; } = new();

    /// <summary>
    /// The output-resolution envelope PixelSnap clamps render size to, or null to fall back to the workflow's own
    /// <c>ResolutionEnvelope</c>. It lives on the configuration because a model file carries identity only.
    /// Overriding it is a ConfigOverride, not an edit to the file.
    /// </summary>
    public ModelResolution? Resolution { get; init; }
}
