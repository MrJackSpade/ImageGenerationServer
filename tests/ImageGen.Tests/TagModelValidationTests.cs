using ImageGen.TagModel;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImageGen.Tests;

public sealed class TagModelValidationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public void An_absent_calibration_is_intentionally_uncalibrated()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("imagegen-calibration-");
        try
        {
            Assert.Null(TagModelBundle.LoadCalibration(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_valid_calibration_loads_both_coefficients()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("imagegen-calibration-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "calibration.json"), """{"a":1.25,"b":-0.5}""");

            TagModelBundle.DisplayCalibration calibration =
                Assert.IsType<TagModelBundle.DisplayCalibration>(TagModelBundle.LoadCalibration(directory.FullName));

            Assert.Equal(1.25, calibration.A);
            Assert.Equal(-0.5, calibration.B);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"a\":1}")]
    [InlineData("{\"a\":\"wrong\",\"b\":2}")]
    [InlineData("{\"a\":1,\"b\":null}")]
    [InlineData("{not-json}")]
    public void A_present_malformed_calibration_fails_actionably(string json)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("imagegen-calibration-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "calibration.json"), json);

            InvalidDataException ex = Assert.Throws<InvalidDataException>(
                () => TagModelBundle.LoadCalibration(directory.FullName));

            Assert.Contains("calibration.json", ex.Message, StringComparison.Ordinal);
            Assert.Contains("'a' and 'b'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_same_length_corrupt_cache_entry_is_redownloaded()
    {
        byte[] expected = [1, 2, 3, 4];
        byte[] corrupt = [9, 9, 9, 9];
        await WithArtifactDirectoryAsync(async directory =>
        {
            string target = Path.Combine(directory, "artifact.bin");
            await File.WriteAllBytesAsync(target, corrupt, Ct);
            ArtifactHandler handler = new(Manifest(expected), expected);
            using HttpClient http = new(handler);

            await TagModelArtifacts.EnsureAsync(http, NullLogger.Instance, directory, Ct);

            Assert.Equal(expected, await File.ReadAllBytesAsync(target, Ct));
            Assert.Contains("artifact.bin", handler.Requests);
        });
    }

    [Fact]
    public async Task A_hash_verified_cache_entry_is_not_redownloaded()
    {
        byte[] expected = [1, 2, 3, 4];
        await WithArtifactDirectoryAsync(async directory =>
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "artifact.bin"), expected, Ct);
            ArtifactHandler handler = new(Manifest(expected), [8, 8, 8, 8]);
            using HttpClient http = new(handler);

            await TagModelArtifacts.EnsureAsync(http, NullLogger.Instance, directory, Ct);

            Assert.DoesNotContain("artifact.bin", handler.Requests);
        });
    }

    [Fact]
    public async Task A_bad_fresh_download_never_replaces_the_existing_file()
    {
        byte[] expected = [1, 2, 3, 4];
        byte[] existing = [9, 9, 9, 9];
        byte[] badDownload = [8, 8, 8, 8];
        await WithArtifactDirectoryAsync(async directory =>
        {
            string target = Path.Combine(directory, "artifact.bin");
            await File.WriteAllBytesAsync(target, existing, Ct);
            using HttpClient http = new(new ArtifactHandler(Manifest(expected), badDownload));

            _ = await Assert.ThrowsAsync<InvalidDataException>(
                () => TagModelArtifacts.EnsureAsync(http, NullLogger.Instance, directory, Ct));

            Assert.Equal(existing, await File.ReadAllBytesAsync(target, Ct));
            Assert.False(File.Exists(target + ".part"));
        });
    }

    [Fact]
    public void The_session_rejects_an_empty_conditioning_set_before_running_onnx()
    {
        S2SRec2Session session = (S2SRec2Session)RuntimeHelpers.GetUninitializedObject(typeof(S2SRec2Session));

        ArgumentException ex = Assert.Throws<ArgumentException>(() => session.Forward([], typeMask: 0));

        Assert.Equal("ids", ex.ParamName);
    }

    private static string Manifest(byte[] artifact)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(artifact));
        return JsonSerializer.Serialize(new
        {
            files = new Dictionary<string, object>
            {
                ["artifact.bin"] = new { bytes = artifact.LongLength, sha256 = hash },
            },
        });
    }

    private static async Task WithArtifactDirectoryAsync(Func<string, Task> test)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("imagegen-artifacts-");
        try
        {
            await test(directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class ArtifactHandler(string manifest, byte[] artifact) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string name = Path.GetFileName(request.RequestUri?.AbsolutePath ?? string.Empty);
            Requests.Add(name);
            byte[] payload = name == "manifest.json" ? Encoding.UTF8.GetBytes(manifest) : artifact;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
