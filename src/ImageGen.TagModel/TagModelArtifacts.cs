using ImageGen.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;

namespace ImageGen.TagModel;

/// <summary>
/// Fetches the tag model, which is the one thing the app needs and does not carry in git (~900 MB of weights that
/// belong to the model's release cycle, not this repository's).
///
/// <para>The app does this itself, at startup. Shipping a download script beside the app and then explaining when not
/// to use it is the confusing part; the app knows it needs the file, so the app gets it.</para>
///
/// <para>Downloads land in a <c>.part</c> file and are moved into place only after their checksum matches, so an
/// interrupted run cannot leave a truncated artifact that looks complete on the next one. A cached file is reused only
/// when both its size and SHA-256 match the current manifest.</para>
/// </summary>
public static class TagModelArtifacts
{
    /// <summary>Where the published artifacts are fetched from.</summary>
    private static class Source
    {
        public const string PublishedAt = "https://huggingface.co/mrjackspade/s2srec2-booru-tags/resolve/main";
    }

    /// <summary>The published artifact file names.</summary>
    private static class Files
    {
        /// <summary>The manifest file's published name.</summary>
        public const string ManifestFileName = "manifest.json";
    }

    /// <summary>The manifest's JSON property names.</summary>
    private static class Manifest
    {
        /// <summary>Manifest key holding the per-artifact table.</summary>
        public const string FilesProperty = "files";

        /// <summary>Manifest key holding an artifact's checksum.</summary>
        public const string Sha256Property = "sha256";

        /// <summary>Manifest key holding an artifact's byte length.</summary>
        public const string BytesProperty = "bytes";
    }

    private sealed record ManifestEntry(long Bytes, string Sha256);

    /// <summary>
    /// Make sure every published artifact is present and verified in <paramref name="directory"/> — the shared cache or
    /// the pre-cache, resolved by the caller via <see cref="TagModelServiceCollectionExtensions.ArtifactsDirectory(bool)"/>.
    ///
    /// <para>Nothing is caught here. A failure to fetch the model is a failure to start: the alternative is an app
    /// that runs with a tag box that silently does nothing, which is far harder to diagnose than a startup error
    /// naming the file and the reason.</para>
    /// </summary>
    [AllowMagicStrings("log message templates")]
    public static async Task EnsureAsync(HttpClient http, ILogger logger, string directory, CancellationToken ct)
    {
        _ = Directory.CreateDirectory(directory);

        // The manifest carries the size and hash every other file is checked against, so it comes first — and it is
        // re-fetched every time, because it is small and it is what tells us whether the rest is current.
        string manifestPath = Path.Combine(directory, Files.ManifestFileName);
        await DownloadAsync(http, Files.ManifestFileName, manifestPath, logger, ct);

        Dictionary<string, ManifestEntry> files = ReadManifest(manifestPath);
        int fetched = 0;
        foreach ((string? name, ManifestEntry? entry) in files)
        {
            string target = Path.Combine(directory, name);
            if (await MatchesAsync(target, entry, ct))
            {
                continue;
            }

            if (fetched == 0)
            {
                logger.LogInformation("Tag model: fetching artifacts into {Directory} (~900 MB, once).", directory);
            }

            await DownloadAsync(http, name, target, logger, ct, entry);
            fetched++;
        }

        if (fetched > 0)
        {
            logger.LogInformation("Tag model: {Count} artifact(s) fetched and verified.", fetched);
        }
    }

    private static async Task<bool> MatchesAsync(string path, ManifestEntry expected, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected.Bytes)
        {
            return false;
        }

        string actual = await Sha256Async(path, ct);
        return string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static Dictionary<string, ManifestEntry> ReadManifest(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty(Manifest.FilesProperty, out JsonElement files))
        {
            throw new InvalidDataException($"'{path}' has no 'files' section; it is not a tag model manifest.");
        }

        Dictionary<string, ManifestEntry> result = new(StringComparer.Ordinal);
        foreach (JsonProperty file in files.EnumerateObject())
        {
            string? sha256 = file.Value.GetProperty(Manifest.Sha256Property).GetString();
            if (string.IsNullOrEmpty(sha256))
            {
                throw new InvalidDataException(
                    $"'{path}': manifest entry '{file.Name}' has no sha256. Every artifact must carry a checksum — "
                    + "an entry without one would download unverified.");
            }

            result[file.Name] = new ManifestEntry(file.Value.GetProperty(Manifest.BytesProperty).GetInt64(), sha256);
        }

        return result;
    }

    [AllowMagicStrings("log message template")]
    private static async Task DownloadAsync(
        HttpClient http, string name, string target, ILogger logger, CancellationToken ct, ManifestEntry? expected = null)
    {
        string partial = target + ".part";
        logger.LogInformation("Tag model: downloading {Name}.", name);
        try
        {
            using (HttpResponseMessage response = await http.GetAsync($"{Source.PublishedAt}/{name}", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                _ = response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync(ct);
                await using FileStream destination = File.Create(partial);
                await source.CopyToAsync(destination, ct);
            }

            if (expected is not null && !string.IsNullOrEmpty(expected.Sha256))
            {
                string actual = await Sha256Async(partial, ct);
                if (!string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"'{name}' failed its checksum (expected {expected.Sha256}, got {actual}).");
                }
            }

            File.Move(partial, target, overwrite: true);
        }
        catch
        {
            // A failed attempt never replaces the verified target. Remove its private staging file and carry the
            // exception on so startup names the artifact that could not be installed.
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }

            throw;
        }
    }
}
