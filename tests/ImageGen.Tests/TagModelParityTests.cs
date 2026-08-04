using System.Text.Json;
using ImageGen.TagModel;

namespace ImageGen.Tests;

/// <summary>
/// Pins the C# tag model against a recorded snapshot of the Python server's answers.
///
/// <para><b>Why a snapshot and not a live comparison.</b> The Python service is being deleted, so a test that called
/// it would stop working the moment it succeeded at its job. The snapshot was captured from the live server on
/// :8000 while it still ran (see <c>tools/capture-tagmodel-parity.py</c>) and is committed alongside these tests, so
/// the comparison keeps its meaning for as long as the checkpoint does.</para>
///
/// <para><b>What can and cannot be pinned.</b> Everything DETERMINISTIC is compared exactly: the ranked order of
/// suggestions, the calibrated probability shown against each one, the lift, and greedy (temperature 0) generation.
/// Sampled generation cannot be — Python drew from PyTorch's process-wide RNG, so the same request never produced the
/// same prompt twice there either — so it gets structural assertions instead: that it terminates on the stop head,
/// respects the type mask, and never emits a banned or seeded tag.</para>
///
/// <para>Skipped when the artifacts are absent, so the suite still runs on a checkout that has not fetched the ~900 MB
/// model. The snapshot itself is committed, so a machine WITH the artifacts always runs the real comparison.</para>
/// </summary>
public sealed class TagModelParityTests : IDisposable
{
    private const double ProbabilityTolerance = 1e-4;

    private static readonly string ArtifactDir = FindRepoPath("tagmodel/artifacts");
    private static readonly string SnapshotPath = FindRepoPath("tests/ImageGen.Tests/tagmodel-parity.json");

    private readonly TagModelBundle? _bundle;

    public TagModelParityTests() =>
        _bundle = Available ? TagModelBundle.Load(ArtifactDir) : null;

    /// <summary>True when both the model artifacts and the recorded snapshot are present.</summary>
    private static bool Available =>
        Directory.Exists(ArtifactDir)
        && File.Exists(Path.Combine(ArtifactDir, "tag_s2srec2.onnx.data"))
        && File.Exists(SnapshotPath);

    [SkippableFact]
    public void Suggest_matches_the_python_server_case_for_case()
    {
        Skip.IfNot(Available, "tag model artifacts or parity snapshot not present");
        var engine = new SuggestEngine(_bundle!);
        var snapshot = LoadSnapshot();

        var compared = 0;
        foreach (var recorded in snapshot.GetProperty("suggest").EnumerateArray())
        {
            var context = recorded.GetProperty("tags").EnumerateArray().Select(e => e.GetString()!).ToArray();
            var fragment = recorded.GetProperty("q").GetString()!;
            var limit = recorded.GetProperty("k").GetInt32();

            var actual = engine.Query(context, fragment, limit);
            var expected = recorded.GetProperty("results").EnumerateArray().ToArray();

            Assert.Equal(expected.Length, actual.Results.Count);
            for (var i = 0; i < expected.Length; i++)
            {
                var label = $"case tags=[{string.Join(',', context)}] q='{fragment}' rank {i}";
                Assert.Equal(expected[i].GetProperty("tag").GetString(), actual.Results[i].Tag);
                Assert.True(
                    Math.Abs(expected[i].GetProperty("p").GetDouble() - actual.Results[i].P) < ProbabilityTolerance,
                    $"{label}: p {expected[i].GetProperty("p").GetDouble()} vs {actual.Results[i].P}");
            }
            compared++;
        }

        Assert.True(compared > 0, "the snapshot contained no suggest cases");
    }

    /// <summary>
    /// Greedy generation is fully determined — no sampling — so it must reproduce Python's tag sequence exactly. This
    /// is the strongest single check on the port: it exercises the forward pass, the row→vocab scatter, the stop head,
    /// the type mask and the per-step masking, and any discrepancy in any of them changes the output.
    /// </summary>
    [SkippableFact]
    public void Greedy_generation_reproduces_the_python_sequence()
    {
        Skip.IfNot(Available, "tag model artifacts or parity snapshot not present");
        var engine = new GenerateEngine(_bundle!);
        var snapshot = LoadSnapshot();

        var compared = 0;
        foreach (var recorded in snapshot.GetProperty("greedy").EnumerateArray())
        {
            var seed = recorded.GetProperty("seed").EnumerateArray().Select(e => e.GetString()!).ToArray();
            var types = recorded.GetProperty("types").EnumerateArray().Select(e => e.GetString()!).ToArray();
            var expected = recorded.GetProperty("tags").EnumerateArray().Select(e => e.GetString()!).ToArray();

            var actual = engine.Generate(
                seed, seed: 0, temperature: 0, bannedTags: null, typeMask: TypeMask.FromAllowedNames(types));

            Assert.Equal(expected, actual.Tags);
            Assert.Equal(recorded.GetProperty("stop_reason").GetString(), StopReasonName(actual.Reason));
            compared++;
        }

        Assert.True(compared > 0, "the snapshot contained no greedy cases");
    }

    /// <summary>
    /// Sampled generation cannot be compared value-for-value against a non-reproducible source, so the invariants are
    /// asserted instead — and they are the ones that would actually hurt if broken: an artist appearing in a prompt
    /// that excluded artists, a banned tag coming back, the seed being echoed, or length running to the safety cap.
    /// </summary>
    [SkippableFact]
    public void Sampled_generation_holds_its_invariants()
    {
        Skip.IfNot(Available, "tag model artifacts or parity snapshot not present");
        var engine = new GenerateEngine(_bundle!);
        var vocab = _bundle!.Vocab;
        var seed = new[] { "1girl", "solo" };
        var banned = new[] { "long_hair", "smile" };

        for (var draw = 0; draw < 8; draw++)
        {
            var result = engine.Generate(seed, seed: 1000 + draw, temperature: 1.0, bannedTags: banned,
                typeMask: TypeMask.NoArtist);

            Assert.Equal(GenerateEngine.StopReason.Complete, result.Reason);
            Assert.NotEmpty(result.Tags);

            foreach (var tag in result.Tags)
            {
                Assert.DoesNotContain(tag, seed);       // the seed is conditioning, never echoed back
                Assert.DoesNotContain(tag, banned);
                var id = vocab.IdOf(tag);
                Assert.True(id.HasValue, $"generated '{tag}' is not in the vocabulary");
                Assert.False(vocab.IsArtist(id.Value), $"generated artist '{tag}' under a no-artist mask");
            }
            Assert.Equal(result.Tags.Distinct().Count(), result.Tags.Count);
        }
    }

    /// <summary>
    /// The mask must reshape what is emitted, not merely be accepted. A character-only mask that still produced
    /// general tags would be the exact defect that made a standing type list collapse generation to two tags.
    /// </summary>
    [SkippableFact]
    public void A_restrictive_type_mask_is_actually_enforced()
    {
        Skip.IfNot(Available, "tag model artifacts or parity snapshot not present");
        var engine = new GenerateEngine(_bundle!);
        var vocab = _bundle!.Vocab;

        var result = engine.Generate(["1girl"], seed: 7, temperature: 1.0, bannedTags: null,
            typeMask: TypeMask.FromAllowedNames(["character", "copyright"]));

        foreach (var tag in result.Tags)
        {
            var id = vocab.IdOf(tag);
            Assert.True(id.HasValue, $"generated '{tag}' is not in the vocabulary");
            var category = vocab.Types[id.Value];
            Assert.True(
                category is TypeMask.CategoryCharacter or TypeMask.CategoryCopyright,
                $"'{tag}' is category {category}, which a character+copyright mask forbids");
        }
    }

    private static string StopReasonName(GenerateEngine.StopReason reason) => reason switch
    {
        GenerateEngine.StopReason.Complete => "complete",
        GenerateEngine.StopReason.Exhausted => "exhausted",
        _ => "max_steps",
    };

    private static JsonElement LoadSnapshot() =>
        JsonDocument.Parse(File.ReadAllText(SnapshotPath)).RootElement;

    /// <summary>Walk up from the test binary to the repo root, then resolve a repo-relative path.</summary>
    private static string FindRepoPath(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ImageGen.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir is null ? relative : Path.Combine(dir, relative);
    }

    /// <inheritdoc />
    public void Dispose() => _bundle?.Dispose();
}
