using ImageGen.Comfy;
using ImageGen.Comfy.Patches;
using ImageGen.Web.Comfy;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// The patch engine, against real directories on disk.
///
/// <para>Everything here is about the two claims the settings page rests on: that applying and then removing a
/// patch leaves the tree BYTE-IDENTICAL, and that a patch which does not fit writes NOTHING. Both are the
/// difference between a page that manages a ComfyUI installation and one that quietly corrupts it, and neither
/// can be established by a test that only checks the happy path.</para>
/// </summary>
public sealed class ComfyPatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "imagegen-patch-" + Guid.NewGuid().ToString("N"));

    public ComfyPatchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string relative, string content)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(full) ?? throw new InvalidOperationException($"'{full}' has no parent directory."));
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Every file under the root, with its bytes — for asserting a tree is unchanged.</summary>
    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                 .ToDictionary(f => Path.GetRelativePath(_root, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);

    private static readonly string Diff = """
        --- a/greet.py
        +++ b/greet.py
        @@ -1,3 +1,4 @@
         def greet(name):
        +    name = name.strip()
             return "hi " + name

        """;

    [Fact]
    public void Apply_then_reverse_leaves_the_file_byte_identical()
    {
        _ = Write("greet.py", "def greet(name):\n    return \"hi \" + name\n\n");
        Dictionary<string, string> before = Snapshot();
        IReadOnlyList<FileDiff> files = UnifiedDiff.Parse(Diff);

        PatchApplier.Apply(_root, files, reverse: false);
        Assert.Contains("name.strip()", File.ReadAllText(Path.Combine(_root, "greet.py")));

        PatchApplier.Apply(_root, files, reverse: true);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void A_patch_is_applied_exactly_when_it_reverse_applies()
    {
        _ = Write("greet.py", "def greet(name):\n    return \"hi \" + name\n\n");
        IReadOnlyList<FileDiff> files = UnifiedDiff.Parse(Diff);

        Assert.True(PatchApplier.Probe(_root, files, reverse: false).Ok);
        Assert.False(PatchApplier.Probe(_root, files, reverse: true).Ok);

        PatchApplier.Apply(_root, files, reverse: false);

        Assert.True(PatchApplier.Probe(_root, files, reverse: true).Ok);
        Assert.False(PatchApplier.Probe(_root, files, reverse: false).Ok);
    }

    /// <summary>
    /// Upstream moves code around constantly. A hunk whose context is intact but has shifted must still apply, or
    /// every patch breaks on the next ComfyUI release for no reason that matters.
    /// </summary>
    [Fact]
    public void A_hunk_still_applies_when_its_context_has_moved()
    {
        _ = Write("greet.py", "# added upstream\n# and another\n\ndef greet(name):\n    return \"hi \" + name\n\n");

        PatchApplier.Apply(_root, UnifiedDiff.Parse(Diff), reverse: false);

        string text = File.ReadAllText(Path.Combine(_root, "greet.py"));
        Assert.Contains("name.strip()", text);
        Assert.StartsWith("# added upstream", text);
    }

    [Fact]
    public void A_hunk_whose_context_has_changed_is_a_conflict_and_writes_nothing()
    {
        _ = Write("greet.py", "def greet(name):\n    return \"hello \" + name\n\n");   // the returned line differs
        Dictionary<string, string> before = Snapshot();
        IReadOnlyList<FileDiff> files = UnifiedDiff.Parse(Diff);

        PatchProbe probe = PatchApplier.Probe(_root, files, reverse: false);
        Assert.False(probe.Ok);
        Assert.Contains("greet.py", probe.Reason);

        _ = Assert.Throws<PatchConflictException>(() => PatchApplier.Apply(_root, files, reverse: false));
        Assert.Equal(before, Snapshot());
    }

    /// <summary>
    /// The all-or-nothing claim. A patch touching two files where only the second refuses must leave the FIRST
    /// alone — a half-applied patch is the state with no way back and no way to diagnose it.
    /// </summary>
    [Fact]
    public void A_patch_that_fails_on_its_second_file_does_not_write_the_first()
    {
        _ = Write("one.py", "a\nb\nc\n");
        _ = Write("two.py", "SOMETHING ELSE\n");
        Dictionary<string, string> before = Snapshot();

        IReadOnlyList<FileDiff> files = UnifiedDiff.Parse("""
            --- a/one.py
            +++ b/one.py
            @@ -1,3 +1,4 @@
             a
            +inserted
             b
             c
            --- a/two.py
            +++ b/two.py
            @@ -1,1 +1,2 @@
             x
            +y
            """);

        _ = Assert.Throws<PatchConflictException>(() => PatchApplier.Apply(_root, files, reverse: false));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void A_created_file_is_removed_again_and_takes_its_bytecode_with_it()
    {
        FileDiff pack = UnifiedDiff.Added("nodes.py", "NODE_CLASS_MAPPINGS = {}\n");
        string target = Path.Combine(_root, "pack");

        PatchApplier.Apply(target, [pack], reverse: false);
        Assert.True(File.Exists(Path.Combine(target, "nodes.py")));

        // Python writes this where the pack ran. It is not content, and a directory left holding only bytecode
        // still looks like an installed pack.
        _ = Directory.CreateDirectory(Path.Combine(target, "__pycache__"));
        File.WriteAllText(Path.Combine(target, "__pycache__", "nodes.pyc"), "");

        PatchApplier.Apply(target, [pack], reverse: true);
        Assert.True(PatchApplier.RemoveIfSpent(target));
        Assert.False(Directory.Exists(target));
    }

    /// <summary>
    /// A pack file that is already there holding something else is somebody's, until they say otherwise. This is
    /// the ordinary state of an installation that has fallen behind the shipped packs.
    /// </summary>
    [Fact]
    public void Creating_over_a_different_file_needs_overwrite_and_names_what_it_would_lose()
    {
        FileDiff pack = UnifiedDiff.Added("nodes.py", "NEW\n");
        _ = Write("nodes.py", "SOMEONE ELSE'S\n");

        Assert.Equal(["nodes.py"], PatchApplier.Occupied(_root, [pack]));

        PatchConflictException conflict = Assert.Throws<PatchConflictException>(() => PatchApplier.Apply(_root, [pack], reverse: false));
        Assert.Contains("nodes.py", conflict.Message);
        Assert.Equal("SOMEONE ELSE'S\n", File.ReadAllText(Path.Combine(_root, "nodes.py")));

        PatchApplier.Apply(_root, [pack], reverse: false, overwrite: true);
        Assert.Equal("NEW\n", File.ReadAllText(Path.Combine(_root, "nodes.py")));
    }

    /// <summary>
    /// A file already holding exactly what the patch installs is not a conflict — that IS an applied pack, and
    /// treating it as occupied would report every correctly-installed pack as broken.
    /// </summary>
    [Fact]
    public void Creating_over_an_identical_file_is_not_a_conflict()
    {
        FileDiff pack = UnifiedDiff.Added("nodes.py", "SAME\n");
        _ = Write("nodes.py", "SAME\n");

        Assert.Empty(PatchApplier.Occupied(_root, [pack]));
        Assert.True(PatchApplier.Probe(_root, [pack], reverse: false).Ok);
    }

    /// <summary>
    /// A Windows checkout of ComfyUI is CRLF throughout and a patch written on Linux is not. Refusing over that
    /// would make every patch conflict on half the installs, and rewriting the file's endings would make every
    /// line of it show up as changed to whatever else reads it.
    /// </summary>
    [Fact]
    public void Line_endings_of_the_destination_are_matched_and_kept()
    {
        _ = Write("greet.py", "def greet(name):\r\n    return \"hi \" + name\r\n\r\n");

        PatchApplier.Apply(_root, UnifiedDiff.Parse(Diff), reverse: false);

        string text = File.ReadAllText(Path.Combine(_root, "greet.py"));
        Assert.Contains("name.strip()", text);
        Assert.DoesNotContain(text.Replace("\r\n", ""), "\n");   // nothing left as a bare LF
    }

    /// <summary>
    /// A pack may carry an asset it cannot run without — sketchKeras ships a 71 MB .pth. Such a file has no
    /// lines, so it is carried whole: present with these exact bytes or not present. The round-trip has to be
    /// byte-perfect, because "close enough" for a tensor file is a model that loads and produces nonsense.
    /// </summary>
    [Fact]
    public void A_carried_binary_file_installs_and_uninstalls_byte_for_byte()
    {
        // Deliberately full of things that break text handling: NULs, a lone CR, a lone LF, high bytes.
        byte[] bytes = new byte[512];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 256);
        }

        bytes[10] = 0x00;
        bytes[11] = 0x0D;
        bytes[12] = 0x0A;
        bytes[13] = 0x00;

        FileDiff pack = UnifiedDiff.AddedBinary("weights/model.pth", bytes);
        Assert.True(pack.IsBinary);
        string target = Path.Combine(_root, "pack");

        PatchApplier.Apply(target, [pack], reverse: false);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(target, "weights", "model.pth")));

        // Reads back as applied, and is not mistaken for someone else's file.
        Assert.True(PatchApplier.Probe(target, [pack], reverse: true).Ok);
        Assert.Empty(PatchApplier.Occupied(target, [pack]));

        PatchApplier.Apply(target, [pack], reverse: true);
        Assert.False(File.Exists(Path.Combine(target, "weights", "model.pth")));
    }

    /// <summary>A carried file that has been replaced is somebody else's, exactly as for text.</summary>
    [Fact]
    public void A_carried_binary_file_that_differs_is_not_overwritten_silently()
    {
        FileDiff pack = UnifiedDiff.AddedBinary("model.pth", [1, 2, 3, 0, 4]);
        _ = Write("model.pth", "");
        File.WriteAllBytes(Path.Combine(_root, "model.pth"), [9, 9, 0, 9]);

        Assert.Equal(["model.pth"], PatchApplier.Occupied(_root, [pack]));
        _ = Assert.Throws<PatchConflictException>(() => PatchApplier.Apply(_root, [pack], reverse: false));
        Assert.Equal(new byte[] { 9, 9, 0, 9 }, File.ReadAllBytes(Path.Combine(_root, "model.pth")));

        PatchApplier.Apply(_root, [pack], reverse: false, overwrite: true);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 4 }, File.ReadAllBytes(Path.Combine(_root, "model.pth")));
    }

    [Fact]
    public void A_path_that_leaves_the_target_directory_is_refused()
    {
        UnifiedDiff.FormatException ex = Assert.Throws<UnifiedDiff.FormatException>(() => UnifiedDiff.Parse("""
            --- a/../../escape.py
            +++ b/../../escape.py
            @@ -1,1 +1,2 @@
             x
            +y
            """));
        Assert.Contains("leaves the target directory", ex.Message);
    }

    [Fact]
    public void A_binary_patch_is_refused_rather_than_guessed_at()
    {
        UnifiedDiff.FormatException ex = Assert.Throws<UnifiedDiff.FormatException>(() =>
            UnifiedDiff.Parse("diff --git a/x.bin b/x.bin\nGIT binary patch\nliteral 0\n"));
        Assert.Contains("binary", ex.Message);
    }

    /// <summary>A commit archive is fetched from the pinned revision, never a branch.</summary>
    [Fact]
    public void The_pack_archive_url_is_the_pinned_commit()
    {
        Assert.Equal(
            "https://codeload.github.com/owner/repo/tar.gz/abc123",
            PackSource.ArchiveUrl("https://github.com/owner/repo.git", "abc123").ToString());

        _ = Assert.Throws<PackSource.FetchException>(() => PackSource.ArchiveUrl("https://gitlab.com/owner/repo", "abc123"));
    }

    /// <summary>
    /// The patch set this build actually ships must parse, carry its metadata, and address each patch uniquely —
    /// an id is what the API takes, and a duplicate would make one of them unreachable.
    /// </summary>
    [Fact]
    public void The_shipped_patch_set_loads()
    {
        string payload = RequiredPayload();

        IReadOnlyList<ComfyPatch> patches = ComfyPatchCatalog.Load(Path.Combine(payload, "comfy-patches"), Path.Combine(payload, "comfy-nodes"));

        Assert.NotEmpty(patches);
        Assert.Equal(patches.Select(p => p.Id).Distinct().Count(), patches.Count);
        Assert.All(patches, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.False(string.IsNullOrWhiteSpace(p.Why));
            // The page shows this under the name. A patch whose description is as terse as its title tells the
            // person deciding whether to apply it nothing they did not already have.
            Assert.False(string.IsNullOrWhiteSpace(p.Does));
            Assert.True(p.Does.Length > p.Title.Length, $"{p.Id}: Does: says no more than its title.");
            // A patch with no diff must be an install-only one; anything else would be a patch that does nothing.
            if (p.Files.Count == 0)
            {
                Assert.True(p.IsInstallOnly, $"{p.Id} has no diff and no Source");
            }
            // A patch that declares where to fetch its target must pin the revision it was written against.
            if (p.SourceUrl is not null)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Rev));
            }
        });

        // The gate changes a guarantee rather than a feature, so removing it has to warn.
        ComfyPatch gate = patches.Single(p => p.Id == "node-imagegen-gate");
        Assert.False(string.IsNullOrWhiteSpace(gate.Warn));
    }

    /// <summary>
    /// Every shipped node pack must survive being installed and removed with the tree unchanged. This is the
    /// property the Remove button on the settings page is, run against the real packs rather than a fixture.
    /// </summary>
    [Fact]
    public void Every_shipped_node_pack_installs_and_uninstalls_cleanly()
    {
        string payload = RequiredPayload();

        IReadOnlyList<ComfyPatch> patches = ComfyPatchCatalog.Load(null, Path.Combine(payload, "comfy-nodes"));
        Assert.NotEmpty(patches);

        foreach (ComfyPatch patch in patches)
        {
            string target = Path.Combine(_root, patch.Target.Replace('/', Path.DirectorySeparatorChar));

            PatchApplier.Apply(target, patch.Files, reverse: false);
            Assert.True(PatchApplier.Probe(target, patch.Files, reverse: true).Ok, $"{patch.Id} did not read back as applied");

            PatchApplier.Apply(target, patch.Files, reverse: true);
            _ = PatchApplier.RemoveIfSpent(target);
            Assert.False(Directory.Exists(target), $"{patch.Id} left {patch.Target} behind");
        }
    }

    /// <summary>
    /// Every third-party pack the app depends on must be installable from the patch set, whether or not this
    /// repo changes anything in it. "We changed nothing" is not "it need not be here": a pack that only ever
    /// arrived by hand is a dependency nothing can state or reinstall, and a rebuilt ComfyUI comes back missing
    /// the packs a dozen workflows need. Every one of them therefore carries a pinned Source/Rev.
    /// </summary>
    [Fact]
    public void Every_third_party_pack_is_installable_from_a_pinned_revision()
    {
        string payload = RequiredPayload();

        IReadOnlyList<ComfyPatch> patches = ComfyPatchCatalog.Load(Path.Combine(payload, "comfy-patches"), Path.Combine(payload, "comfy-nodes"));

        // Anything targeting custom_nodes/ must either BE the pack (this repo ships it) or say where to get it.
        List<ComfyPatch> packs = [.. patches.Where(p => p.Target.StartsWith("custom_nodes/", StringComparison.Ordinal))];
        Assert.NotEmpty(packs);
        Assert.All(packs, p => Assert.True(p.CreatesItsTarget || p.SourceUrl is not null,
            $"{p.Id} targets {p.Target} but neither ships it nor says where to fetch it"));

        // An install-only patch is exactly that: a pinned source and no diff of ours.
        foreach (ComfyPatch? p in packs.Where(p => p.IsInstallOnly))
        {
            Assert.Empty(p.Files);
            Assert.Matches("^[0-9a-f]{40}$", p.Rev ?? throw new InvalidOperationException("patch has no rev"));   // a commit, never a branch
        }

        // The two packs whose absence takes workflows out of the catalogue entirely.
        Assert.Contains(packs, p => p.Target == "custom_nodes/ComfyUI-PixelHarness");
        Assert.Contains(packs, p => p.Target == "custom_nodes/ComfyUI-Anima-LLLite");
    }

    /// <summary>
    /// A patch's <c>Provides:</c> must name a catalogue requirement that actually exists. Nothing fails when it
    /// does not — the models page and the workflow dialog simply never offer the install button — so a typo here
    /// is invisible until somebody wonders why a missing pack has no way to be installed. This is the only place
    /// the two halves are checked against each other.
    /// </summary>
    [SkippableFact]
    public void Every_provides_names_a_real_catalogue_requirement()
    {
        string? repo = RepositoryRoot();
        Skip.If(repo is null, "not running from a source checkout");
        Assert.NotNull(repo);

        IReadOnlyList<ComfyPatch> patches = ComfyPatchCatalog.Load(Path.Combine(repo, "comfy-patches"), Path.Combine(repo, "comfy-nodes"));

        Dictionary<string, string?> slots = Directory.EnumerateFiles(Path.Combine(repo, "configurations", "models"), "*.json")
            .Select(f => System.Text.Json.JsonDocument.Parse(File.ReadAllText(f)).RootElement)
            .Where(e => e.TryGetProperty("id", out _))
            .ToDictionary(e => e.GetProperty("id").RequireString(), e => e.TryGetProperty("kind", out JsonElement k) ? k.GetString() : null);

        foreach (ComfyPatch patch in patches)
        {
            foreach (string provided in patch.Provides)
            {
                Assert.True(slots.ContainsKey(provided),
                    $"{patch.Id} says it provides '{provided}', which is not a catalogue requirement.");

                // Only a custom_node requirement can be satisfied by installing code. A model file is weights,
                // and no patch ever carries weights.
                Assert.Equal("custom_node", slots[provided]);
            }
        }

        // Every custom_node requirement the catalogue has should be installable, or a workflow needing it is a
        // dead end on a fresh box.
        HashSet<string> installable = patches.SelectMany(p => p.Provides).ToHashSet(StringComparer.Ordinal);
        List<string> orphans = [.. slots.Where(s => s.Value == "custom_node" && !installable.Contains(s.Key)).Select(s => s.Key)];
        Assert.True(orphans.Count == 0, $"no patch installs these custom_node requirements: {string.Join(", ", orphans)}");
    }

    /// <summary>
    /// Against a LIVE ComfyUI: the renderer folder can be derived from the renderer itself and confirmed on this
    /// filesystem. Opt-in via COMFY_URL, because it needs a running backend — but it is the only thing that
    /// proves the derivation against a real <c>/internal/folder_paths</c>, which is not ComfyUI's documented API
    /// and is exactly the sort of thing that changes under us.
    /// </summary>
    [SkippableFact]
    public async Task Live_renderer_reports_a_folder_this_machine_can_verify()
    {
        string? baseUrl = Environment.GetEnvironmentVariable("COMFY_URL");
        Skip.If(string.IsNullOrWhiteSpace(baseUrl), "set COMFY_URL to run this against a live ComfyUI");
        Assert.NotNull(baseUrl);

        ComfyInstall install = new(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new SingleClientFactory(),
            new FixedEndpoint(baseUrl));

        string? root = await install.DetectRootAsync(CancellationToken.None);

        Assert.NotNull(root);
        Assert.True(File.Exists(Path.Combine(root, "main.py")), $"{root} has no main.py");
        Assert.True(Directory.Exists(Path.Combine(root, "comfy")), $"{root} has no comfy/");
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FixedEndpoint(string baseUrl) : IComfyEndpoint
    {
        public string BaseUrl { get; } = baseUrl;
        public string GateToken => "";
    }

    /// <summary>The source checkout this test is running out of, found by walking up from the test binary.</summary>
    private static string? RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "comfy-patches")) &&
                Directory.Exists(Path.Combine(directory.FullName, "configurations", "models")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>The payload directory copied beside the test binary. Its absence is a broken build, not a skip.</summary>
    private static string RequiredPayload()
    {
        string nodes = Path.Combine(AppContext.BaseDirectory, "comfy-nodes");
        string patches = Path.Combine(AppContext.BaseDirectory, "comfy-patches");
        Assert.True(Directory.Exists(nodes), $"The build did not copy its comfy-nodes payload to {nodes}.");
        Assert.True(Directory.Exists(patches), $"The build did not copy its comfy-patches payload to {patches}.");
        return AppContext.BaseDirectory;
    }
}
