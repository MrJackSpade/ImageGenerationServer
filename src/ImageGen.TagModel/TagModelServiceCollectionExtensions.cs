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
        /// <summary>Folder beside the executable that holds the tag model.</summary>
        public const string TagModelFolder = "tagmodel";

        /// <summary>Sub-folder holding the published artifacts.</summary>
        public const string ArtifactsFolder = "artifacts";
    }

    /// <summary>
    /// Where the artifacts live: <c>tagmodel/artifacts</c> beside the executable, always.
    ///
    /// <para>Not configurable, and anchored to the application rather than to the working directory. A configurable
    /// relative path (repeated across a config key, the settings page, an install-script flag and both launchers)
    /// would resolve against the CURRENT DIRECTORY, so launching the executable from anywhere but its own folder
    /// would fail to find the model and refuse to start, naming a path that does not exist. The install script writes
    /// here, the app reads here, and there is nothing to keep in step.</para>
    /// </summary>
    public static string ArtifactsDirectory => Path.Combine(AppContext.BaseDirectory, Folders.TagModelFolder, Folders.ArtifactsFolder);

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
