using ImageGen.Application.Tags;
using Microsoft.Extensions.DependencyInjection;

namespace ImageGen.TagModel;

/// <summary>
/// Registers the in-process tag model as both tag ports: <see cref="ITagCatalog"/> (the '#'/'@' autocomplete and the
/// random-artist pick) and <see cref="ITagModelClient"/> (context-ranked suggestions and whole-prompt generation).
/// One bundle backs both, so the ~900 MB of weights and the vocabulary are loaded exactly once.
/// </summary>
public static class TagModelServiceCollectionExtensions
{
    /// <summary>Folder names composing the path to the tag model artifacts.</summary>
    private static class Folders
    {
        /// <summary>Per-user cache root subfolder that scopes this app's files.</summary>
        public const string AppFolder = "ImageGenerationServer";

        /// <summary>Folder that holds the tag model.</summary>
        public const string TagModelFolder = "tagmodel";

        /// <summary>Sub-folder holding the published artifacts.</summary>
        public const string ArtifactsFolder = "artifacts";

        /// <summary>Default per-user cache root folder under the home directory when no XDG override is set (Linux).</summary>
        public const string LinuxCacheFolder = ".cache";
    }

    /// <summary>Environment variable names consulted when resolving the cache root.</summary>
    private static class EnvVars
    {
        /// <summary>Names the Linux user cache root (XDG base directory spec).</summary>
        public const string XdgCacheHome = "XDG_CACHE_HOME";
    }

    /// <summary>
    /// Where the artifacts live: a stable per-user cache location under an app-named subfolder — on Windows
    /// <c>%LOCALAPPDATA%\ImageGenerationServer\tagmodel\artifacts</c>, on Linux
    /// <c>$XDG_CACHE_HOME/ImageGenerationServer/tagmodel/artifacts</c> (default <c>~/.cache</c>).
    ///
    /// <para>Anchored to the user's cache rather than to the install folder. A deploy replaces the app folder with a
    /// fresh self-contained build, so caching beside the executable (the old <c>AppContext.BaseDirectory</c> location)
    /// meant re-downloading ~900 MB on every deploy; a per-user path persists across updates and needs no write access
    /// to the install directory. It is an absolute path, so it is independent of the current working directory.</para>
    ///
    /// <para>The cache dir — not the data dir — is correct because these artifacts are re-downloadable and verified
    /// against a manifest. On Windows this is Local AppData, never Roaming: a 900 MB model must not sync across
    /// machines. On Linux, .NET's <see cref="Environment.SpecialFolder.LocalApplicationData"/> maps to
    /// <c>~/.local/share</c> (<c>$XDG_DATA_HOME</c>), not the cache, so the XDG cache root is resolved explicitly.</para>
    /// </summary>
    public static string ArtifactsDirectory => Path.Combine(UserCacheRoot, Folders.AppFolder, Folders.TagModelFolder, Folders.ArtifactsFolder);

    /// <summary>Resolves the per-user cache root, correct per OS (Local AppData on Windows, XDG cache on Linux).</summary>
    private static string UserCacheRoot
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }

            string xdgCache = Environment.GetEnvironmentVariable(EnvVars.XdgCacheHome) ?? string.Empty;
            if (!string.IsNullOrEmpty(xdgCache))
            {
                return xdgCache;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Folders.LinuxCacheFolder);
        }
    }

    /// <summary>
    /// Load the artifacts from <see cref="ArtifactsDirectory"/> and register both tag ports.
    ///
    /// <para>Loading happens HERE, at startup, rather than lazily on first use. The tag model is not optional — a
    /// missing artifact means autocomplete is broken and every random-prompt render fails — so it is far better to
    /// refuse to start with a message naming the missing file than to serve a page whose tag box silently does
    /// nothing.</para>
    /// </summary>
    public static IServiceCollection AddTagModel(this IServiceCollection services)
    {
        TagModelBundle bundle = TagModelBundle.Load(ArtifactsDirectory);

        _ = services.AddSingleton(bundle);
        _ = services.AddSingleton(bundle.Vocab);
        _ = services.AddSingleton<ITagCatalog>(_ => new VocabTagCatalog(bundle.Vocab));
        _ = services.AddSingleton<ITagModelClient>(_ => new OnnxTagModelClient(bundle));
        return services;
    }
}
