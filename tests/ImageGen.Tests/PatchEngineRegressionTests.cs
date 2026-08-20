using ImageGen.Comfy.Patches;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace ImageGen.Tests;

/// <summary>Regression coverage for unified-diff boundaries and patch installation failure guarantees.</summary>
public sealed class PatchEngineRegressionTests : IDisposable
{
    private static class DiffText
    {
        public const string NoNewline = "\\ No newline at end of file";
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "imagegen-patch-regression-" + Guid.NewGuid().ToString("N"));

    public PatchEngineRegressionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Pure_insertion_uses_the_zero_count_anchor_after_the_named_line()
    {
        string path = Path.Combine(_root, "items.txt");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        IReadOnlyList<FileDiff> files = UnifiedDiff.Parse("""
            --- a/items.txt
            +++ b/items.txt
            @@ -2,0 +3,1 @@
            +inserted
            """);

        PatchApplier.Apply(_root, files, reverse: false);

        Assert.Equal("one\ntwo\ninserted\nthree\n", File.ReadAllText(path));
        PatchApplier.Apply(_root, files, reverse: true);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(path));
    }

    [Fact]
    public void No_newline_marker_belongs_only_to_the_immediately_preceding_side()
    {
        string path = Path.Combine(_root, "tail.txt");
        File.WriteAllText(path, "old\n");
        IReadOnlyList<FileDiff> files = UnifiedDiff.Parse("""
            --- a/tail.txt
            +++ b/tail.txt
            @@ -1,1 +1,1 @@
            -old
            +new
            \ No newline at end of file
            """);

        FileDiff file = Assert.Single(files);
        Assert.False(file.OldEndsWithoutNewline);
        Assert.True(file.NewEndsWithoutNewline);

        PatchApplier.Apply(_root, files, reverse: false);
        Assert.Equal("new", File.ReadAllText(path));

        PatchApplier.Apply(_root, files, reverse: true);
        Assert.Equal("old\n", File.ReadAllText(path));
    }

    [Fact]
    public void Writer_emits_no_newline_marker_only_in_the_files_final_hunk()
    {
        FileDiff file = new("tail.txt", FileChange.Modify,
        [
            new Hunk(1, ["a"], 1, ["A"]),
            new Hunk(3, ["c"], 3, ["C"]),
        ])
        {
            NewEndsWithoutNewline = true,
        };

        string written = UnifiedDiff.Write([file]);

        Assert.Equal(1, written.Split(DiffText.NoNewline, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain($"+A\n{DiffText.NoNewline}", written);
        Assert.Contains($"+C\n{DiffText.NoNewline}", written);
        FileDiff parsed = Assert.Single(UnifiedDiff.Parse(written));
        Assert.False(parsed.OldEndsWithoutNewline);
        Assert.True(parsed.NewEndsWithoutNewline);
    }

    [Fact]
    public async Task Redirected_process_drains_pip_sized_stderr_without_deadlocking()
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            string command = "for /L %i in (1,1,1000) do @echo warning-warning-warning-warning-warning-warning-warning-warning 1>&2 & @echo torch==2.0";
            startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            string command = "i=0; while [ \"$i\" -lt 1000 ]; do echo warning-warning-warning-warning-warning-warning-warning-warning >&2; i=$((i + 1)); done; echo torch==2.0";
            startInfo = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        PatchInstaller.ProcessOutput output = await PatchInstaller.RunProcessAsync(startInfo, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, output.ExitCode);
        Assert.Contains("torch==2.0", output.Stdout);
        Assert.True(output.Stderr.Length > 32 * 1024, $"Expected pipe-filling stderr, got {output.Stderr.Length} characters.");
    }

    [Fact]
    public async Task Conflict_after_fetch_removes_the_fetched_pack()
    {
        byte[] archive = Archive(("repository-abc/nodes.py", "different\n"));
        using HttpClient client = new(new StaticHandler(archive));
        PatchInstaller installer = new(new PackSource(new FixedFactory(client)), NullLogger<PatchInstaller>.Instance);
        ComfyPatch patch = new(
            "rollback-test",
            "Rollback test",
            "Exercises cleanup after a fetched patch conflicts.",
            "A failed install must leave no fetched pack behind.",
            "custom_nodes/test-pack",
            "https://github.com/owner/repository.git",
            "abc",
            null,
            0,
            [],
            UnifiedDiff.Parse("""
                --- a/nodes.py
                +++ b/nodes.py
                @@ -1,1 +1,1 @@
                -expected
                +patched
                """));

        _ = await Assert.ThrowsAsync<PatchConflictException>(
            () => installer.ApplyAsync(patch, _root, python: null, overwrite: false, CancellationToken.None));

        Assert.False(Directory.Exists(patch.ResolveTarget(_root)));
        Assert.False(Directory.Exists(patch.ResolveTarget(_root) + ".incoming"));
    }

    private static byte[] Archive(params (string Path, string Content)[] files)
    {
        using MemoryStream tar = new();
        using (TarWriter writer = new(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach ((string path, string content) in files)
            {
                PaxTarEntry entry = new(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                };
                writer.WriteEntry(entry);
            }
        }

        tar.Position = 0;
        using MemoryStream archive = new();
        using (GZipStream gzip = new(archive, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            tar.CopyTo(gzip);
        }

        return archive.ToArray();
    }

    private sealed class FixedFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticHandler(byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive),
            });
    }
}
