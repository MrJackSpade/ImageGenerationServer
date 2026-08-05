using System.Formats.Tar;
using System.IO.Compression;

namespace ImageGen.Comfy.Patches;

/// <summary>
/// Fetches the node pack a patch is written against, at the exact revision it was written against.
///
/// <para>A tarball rather than a clone, deliberately: a release build on Windows has no git, and asking the
/// person who just clicked "Apply" to install one is not an answer. A pinned commit archive is every bit as
/// reproducible as a pinned clone and needs nothing but the HTTP client the app already has.</para>
///
/// <para>What it fetches is always the pinned <c>Rev</c> — never a branch. The diff was taken against that
/// commit; anything else is a patch applied to code it has never seen.</para>
/// </summary>
public sealed class PackSource(IHttpClientFactory httpFactory)
{
    private readonly IHttpClientFactory _httpFactory = httpFactory;

    private const string UserAgent = "ImageGen";
    private const string GitHubHost = "github.com";
    private const string GitHubWwwHost = "www.github.com";
    private const string GitSuffix = ".git";

    public sealed class FetchException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>
    /// Download <paramref name="sourceUrl"/> at <paramref name="rev"/> and unpack it into
    /// <paramref name="destination"/>, which must not already exist.
    /// </summary>
    public async Task FetchAsync(string sourceUrl, string rev, string destination, CancellationToken ct)
    {
        if (Directory.Exists(destination))
            throw new FetchException($"{destination} already exists — nothing was fetched over it.");

        Uri archive = ArchiveUrl(sourceUrl, rev);

        // Unpack beside the destination and move into place at the end, so an interrupted download never
        // leaves a half-extracted pack that looks installed. Same volume, so the move is atomic.
        string staging = destination + ".incoming";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        try
        {
            HttpClient http = _httpFactory.CreateClient();
            // GitHub serves codeload without authentication but requires a user agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            using HttpResponseMessage response = await http.GetAsync(archive, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                string hint = (int)response.StatusCode == 404 ? " The revision or repository could not be found." : "";
                throw new FetchException($"{archive} answered {(int)response.StatusCode}.{hint}");
            }

            Directory.CreateDirectory(staging);
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            await using GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress);
            await ExtractStrippingRootAsync(gzip, staging, ct);

            // A commit archive wraps everything in one "{repo}-{sha}" directory. What we want is its contents.
            string[] entries = Directory.GetFileSystemEntries(staging);
            string root = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staging;

            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException($"'{destination}' has no parent directory."));
            Directory.Move(root, destination);
        }
        catch (Exception ex) when (ex is not FetchException and not OperationCanceledException)
        {
            throw new FetchException($"Could not fetch {archive}: {ex.Message}", ex);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>The commit archive for a GitHub repository URL.</summary>
    internal static Uri ArchiveUrl(string sourceUrl, string rev)
    {
        if (!Uri.TryCreate(sourceUrl.Trim(), UriKind.Absolute, out Uri? uri))
            throw new FetchException($"'{sourceUrl}' is not a repository URL.");

        if (!uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals(GitHubWwwHost, StringComparison.OrdinalIgnoreCase))
            throw new FetchException($"{uri.Host} is not somewhere this knows how to fetch a pinned archive from. Install {sourceUrl} yourself and apply the patch again.");

        string path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase)) path = path[..^4];

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2) throw new FetchException($"'{sourceUrl}' is not an owner/repository URL.");

        return new Uri($"https://codeload.github.com/{segments[0]}/{segments[1]}/tar.gz/{rev}");
    }

    /// <summary>
    /// Extract, refusing any entry that would land outside <paramref name="destination"/>. <c>TarFile</c>'s own
    /// extractor is not used because the root directory has to be stripped, and rolling the loop by hand means
    /// the path check is ours rather than assumed.
    /// </summary>
    private static async Task ExtractStrippingRootAsync(Stream tar, string destination, CancellationToken ct)
    {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        await using TarReader reader = new TarReader(tar);

        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                continue;   // links, devices and the rest have no place in a node pack

            string full = Path.GetFullPath(Path.Combine(destination, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.Ordinal))
                throw new FetchException($"the archive contains '{entry.Name}', which would be written outside the pack directory.");

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(full);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? throw new InvalidOperationException($"'{full}' has no parent directory."));
            await entry.ExtractToFileAsync(full, overwrite: false, ct);
        }
    }
}
