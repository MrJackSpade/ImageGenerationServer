using ImageGen.Comfy;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageGen.Tests;

/// <summary>
/// The matcher that turns "you have some files" into "this workflow is ready", using the published-name patterns
/// in the shipped model files.
///
/// <para>The cases that matter are the ones where it must NOT bind. A slot left visibly empty is a five-second
/// fix in the UI; a slot silently bound to the wrong weights renders wrong images and looks settled.</para>
/// </summary>
public sealed class ModelMatcherTests
{
    private static MatchableSlot Slot(string id, RequirementKind kind, params string[] patterns) =>
        new(id, kind, patterns);

    private static Dictionary<RequirementKind, IReadOnlyList<string>> Files(
        RequirementKind kind, params string[] names) =>
        new() { [kind] = names };

    /// <summary>
    /// The case the whole feature exists for: Civitai's auto-generated filename bears no resemblance to the slot
    /// id, so an exact match finds nothing and the workflow vanishes silently.
    /// </summary>
    [Fact]
    public void A_civitai_renamed_checkpoint_is_recognised_by_its_published_name()
    {
        IReadOnlyList<SlotMatch> result = ModelMatcher.Match(
            [Slot("pony", RequirementKind.Checkpoint, "pony.*diffusion.*v6")],
            Files(RequirementKind.Checkpoint, "ponyDiffusionV6XL_v6StartWithThisOne.safetensors"));

        SlotMatch m = Assert.Single(result);
        Assert.Equal("ponyDiffusionV6XL_v6StartWithThisOne.safetensors", m.AutoBind);
    }

    [Fact]
    public void Matching_ignores_case_and_separators_and_the_extension()
    {
        IReadOnlyList<SlotMatch> result = ModelMatcher.Match(
            [Slot("z", RequirementKind.UnetGguf, "z[-_. ]?image[-_. ]?turbo")],
            Files(RequirementKind.UnetGguf, "Z_Image_Turbo-Q4_K_M.gguf"));

        Assert.Equal("Z_Image_Turbo-Q4_K_M.gguf", Assert.Single(result).AutoBind);
    }

    /// <summary>Two quantisations of one model: the user has to say which they want, so neither is bound.</summary>
    [Fact]
    public void Several_files_matching_one_slot_are_proposed_but_none_is_bound()
    {
        IReadOnlyList<SlotMatch> result = ModelMatcher.Match(
            [Slot("flux", RequirementKind.UnetGguf, "flux1[-_. ]?dev")],
            Files(RequirementKind.UnetGguf, "flux1-dev-Q4_K_S.gguf", "flux1-dev-Q5_K_M.gguf"));

        SlotMatch m = Assert.Single(result);
        Assert.Null(m.AutoBind);
        Assert.Equal(2, m.Candidates.Count);
    }

    /// <summary>
    /// Two slots recognising the same file means the patterns are too loose. Picking one would hide a catalogue
    /// bug behind plausible behaviour on somebody else's disk.
    /// </summary>
    [Fact]
    public void A_file_two_slots_both_claim_is_bound_to_neither()
    {
        IReadOnlyList<SlotMatch> result = ModelMatcher.Match(
            [
                Slot("hd", RequirementKind.Unet, "chroma1[-_. ]?hd"),
                Slot("flash", RequirementKind.Unet, "chroma1[-_. ]?hd[-_. ]?flash"),
            ],
            Files(RequirementKind.Unet, "Chroma1-HD-Flash.safetensors"));

        Assert.Equal(2, result.Count);
        Assert.All(result, m => Assert.Null(m.AutoBind));
        Assert.All(result, m => Assert.Contains("Chroma1-HD-Flash.safetensors", m.Candidates));
    }

    /// <summary>
    /// Matching never crosses loader kinds. In one flat set, a VAE and a checkpoint sharing a name would satisfy
    /// each other's presence check.
    /// </summary>
    [Fact]
    public void A_pattern_never_reaches_into_another_kind()
    {
        Dictionary<RequirementKind, IReadOnlyList<string>> files = new Dictionary<RequirementKind, IReadOnlyList<string>>
        {
            [RequirementKind.Checkpoint] = ["shared-name.safetensors"],
            [RequirementKind.Vae] = ["shared-name.safetensors"],
        };

        IReadOnlyList<SlotMatch> result = ModelMatcher.Match([Slot("ckpt-only", RequirementKind.Checkpoint, "shared[-_. ]?name")], files);

        SlotMatch m = Assert.Single(result);
        Assert.Equal("ckpt-only", m.SlotId);
        Assert.Single(m.Candidates);   // the VAE of the same name is not a candidate
    }

    [Fact]
    public void A_slot_with_no_patterns_is_never_matched()
    {
        IReadOnlyList<SlotMatch> result = ModelMatcher.Match(
            [Slot("unpatterned", RequirementKind.Checkpoint)],
            Files(RequirementKind.Checkpoint, "anything.safetensors"));

        Assert.Empty(result);
    }

    [Fact]
    public void A_slot_whose_patterns_match_nothing_present_is_omitted()
    {
        IReadOnlyList<SlotMatch> result = ModelMatcher.Match(
            [Slot("absent", RequirementKind.Checkpoint, "not.*here")],
            Files(RequirementKind.Checkpoint, "something-else.safetensors"));

        Assert.Empty(result);
    }

    /// <summary>A custom-node directory has no extension and must not have its last dotted segment eaten.</summary>
    [Fact]
    public void A_name_with_no_extension_is_matched_whole()
    {
        IReadOnlyList<SlotMatch> result = ModelMatcher.Match(
            [Slot("node", RequirementKind.SeedVr2, "ComfyUI[-_. ]?SeedVR2")],
            Files(RequirementKind.SeedVr2, "ComfyUI-SeedVR2"));

        Assert.Equal("ComfyUI-SeedVR2", Assert.Single(result).AutoBind);
    }

    /// <summary>Version dots are part of the name; only a real trailing extension comes off.</summary>
    [Fact]
    public void Version_dots_survive_extension_stripping()
    {
        IReadOnlyList<SlotMatch> result = ModelMatcher.Match(
            [Slot("wan", RequirementKind.UnetGguf, @"wan2\.2.*ti2v")],
            Files(RequirementKind.UnetGguf, "Wan2.2-TI2V-5B-Q4_K_M.gguf"));

        Assert.Equal("Wan2.2-TI2V-5B-Q4_K_M.gguf", Assert.Single(result).AutoBind);
    }

    /// <summary>
    /// Patterns can come from a file a user wrote. Compiling without backtracking makes a pathological pattern
    /// impossible rather than slow, at the price of rejecting lookarounds — so the rejection has to name the slot
    /// and the pattern, not surface as a bare regex error.
    /// </summary>
    [Fact]
    public void A_lookahead_is_rejected_with_a_message_naming_the_slot()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            ModelMatcher.Compile(Slot("bad-slot", RequirementKind.Checkpoint, "foo(?!bar)")));

        Assert.Contains("bad-slot", ex.Message);
        Assert.Contains("backtracking", ex.Message);
    }

    [Fact]
    public void An_invalid_pattern_is_rejected_with_a_message_naming_the_slot()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            ModelMatcher.Compile(Slot("broken", RequirementKind.Checkpoint, "unclosed(")));

        Assert.Contains("broken", ex.Message);
    }

    /// <summary>
    /// Walks up from the test binary to the repository root, for the tests below that read the SHIPPED
    /// catalogue rather than hand-written examples — so a pattern that is unusable or too greedy fails here,
    /// rather than on a stranger's disk where the symptom is the wrong weights being loaded.
    /// </summary>
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "configurations", "models")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("configurations/models not found above the test bin dir.");
    }

    private static List<MatchableSlot> ShippedSlots()
    {
        List<MatchableSlot> slots = new List<MatchableSlot>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "configurations", "models"), "*.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            List<string> patterns = root.TryGetProperty("match", out JsonElement m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(e => e.RequireString()).ToList()
                : [];
            // Mirrors ParseKind: an unrecognised kind is a failure, not a bucket. If this throws, a shipped
            // slot names a kind that no loader serves.
            string raw = root.GetProperty("kind").RequireString();
            RequirementKind kind = Enum.TryParse<RequirementKind>(raw.Replace("_", ""), ignoreCase: true, out RequirementKind k)
                ? k
                : throw new InvalidOperationException($"slot kind '{raw}' maps to no RequirementKind");
            slots.Add(new MatchableSlot(root.GetProperty("id").RequireString(), kind, patterns));
        }
        return slots;
    }

    [Fact]
    public void Every_shipped_pattern_compiles_without_backtracking()
    {
        List<MatchableSlot> slots = ShippedSlots();
        Assert.NotEmpty(slots);

        // Compile throws naming the offending slot, which is the whole point — this assertion exists so that
        // message appears in CI rather than in a user's startup log.
        foreach (MatchableSlot slot in slots) ModelMatcher.Compile(slot);
    }

    /// <summary>
    /// No shipped pattern may claim a file belonging to a different slot of the same kind. This is the assertion
    /// that keeps the hand-authored patterns honest as the catalogue grows: adding a loose one for a new model
    /// fails here, next to the model it collides with, instead of silently binding somebody's weights to the
    /// wrong workflow.
    /// </summary>
    [Fact]
    public void No_shipped_pattern_reaches_into_another_slot_of_its_kind()
    {
        List<MatchableSlot> slots = ShippedSlots();
        List<string> collisions = new List<string>();

        foreach (MatchableSlot? slot in slots.Where(s => s.Patterns.Count > 0))
        {
            IReadOnlyList<Regex> regexes = ModelMatcher.Compile(slot);
            foreach (MatchableSlot? other in slots.Where(o => o.Kind == slot.Kind && o.Id != slot.Id))
            {
                // A slot id is the closest thing the catalogue still holds to "what that model is called", now
                // that the author's filenames are gone, so it stands in for the other model's real filename.
                if (regexes.Any(rx => rx.IsMatch(other.Id)))
                    collisions.Add($"{slot.Id} also matches {other.Id}");
            }
        }

        Assert.True(collisions.Count == 0, "Patterns reaching other slots:\n  " + string.Join("\n  ", collisions));
    }
}
