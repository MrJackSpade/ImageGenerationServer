using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ImageGen.TagModel;

/// <summary>
/// Fetches the tag model, which is the one thing the app needs and does not carry in git (~900 MB of weights that
/// belong to the model's release cycle, not this repository's).
///
/// <para>The app does this itself, at startup. Shipping a download script beside the app and then explaining when not
/// to use it is the confusing part; the app knows it needs the file, so the app gets it.</para>
///
/// <para>Downloads land in a <c>.part</c> file and are moved into place only after their checksum matches, so an
/// interrupted run cannot leave a truncated artifact that looks complete on the next one. Nothing is re-downloaded
/// when it is already present at the published size.</para>
/// </summary>
public static class TagModelArtifacts
{
    private const string PublishedAt = "https://huggingface.co/mrjackspade/s2srec2-booru-tags/resolve/main";

    private sealed record ManifestEntry(long Bytes, string Sha256);

    /// <summary>
    /// Make sure every published artifact is present and verified in <see cref="TagModelServiceCollectionExtensions.ArtifactsDirectory"/>.
    ///
    /// <para>Nothing is caught here. A failure to fetch the model is a failure to start: the alternative is an app
    /// that runs with a tag box that silently does nothing, which is far harder to diagnose than a startup error
    /// naming the file and the reason.</para>
    /// </summary>
    public static async Task EnsureAsync(HttpClient http, ILogger logger, CancellationToken ct)
    {
        var directory = TagModelServiceCollectionExtensions.ArtifactsDirectory;
        Directory.CreateDirectory(directory);

        // The manifest carries the size and hash every other file is checked against, so it comes first — and it is
        // re-fetched every time, because it is small and it is what tells us whether the rest is current.
        var manifestPath = Path.Combine(directory, "manifest.json");
        await DownloadAsync(http, "manifest.json", manifestPath, logger, ct);

        var files = ReadManifest(manifestPath);
        var fetched = 0;
        foreach (var (name, entry) in files)
        {
            var target = Path.Combine(directory, name);
            if (File.Exists(target) && new FileInfo(target).Length == entry.Bytes) continue;

            if (fetched == 0)
                logger.LogInformation("Tag model: fetching artifacts into {Directory} (~900 MB, once).", directory);
            await DownloadAsync(http, name, target, logger, ct, entry);
            fetched++;
        }

        if (fetched > 0) logger.LogInformation("Tag model: {Count} artifact(s) fetched and verified.", fetched);
    }

    private static Dictionary<string, ManifestEntry> ReadManifest(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("files", out var files))
            throw new InvalidDataException($"'{path}' has no 'files' section; it is not a tag model manifest.");

        var result = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var file in files.EnumerateObject())
        {
            var sha256 = file.Value.GetProperty("sha256").GetString();
            if (string.IsNullOrEmpty(sha256))
                throw new InvalidDataException(
                    $"'{path}': manifest entry '{file.Name}' has no sha256. Every artifact must carry a checksum — "
                    + "an entry without one would download unverified.");
            result[file.Name] = new ManifestEntry(file.Value.GetProperty("bytes").GetInt64(), sha256);
        }
        return result;
    }

    private static async Task DownloadAsync(
        HttpClient http, string name, string target, ILogger logger, CancellationToken ct, ManifestEntry? expected = null)
    {
        var partial = target + ".part";
        logger.LogInformation("Tag model: downloading {Name}.", name);
        try
        {
            using (var response = await http.GetAsync($"{PublishedAt}/{name}", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = File.Create(partial);
                await source.CopyToAsync(destination, ct);
            }

            if (expected is not null && !string.IsNullOrEmpty(expected.Sha256))
            {
                await using var stream = File.OpenRead(partial);
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                if (!string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"'{name}' failed its checksum (expected {expected.Sha256}, got {actual}).");
            }

            File.Move(partial, target, overwrite: true);
        }
        catch
        {
            // Leaving a truncated .part behind would be indistinguishable from a complete file on the size check
            // above, so the failed attempt is removed and the exception carries on.
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }
}
