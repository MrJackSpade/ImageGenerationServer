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
    /// <summary>
    /// Where the artifacts live: <c>tagmodel/artifacts</c> beside the executable, always.
    ///
    /// <para>Not configurable, and anchored to the application rather than to the working directory. It used to be
    /// <c>TagModel:DataDir</c>, one correct value repeated across the config key, the settings page, the install
    /// script's <c>-DataDir</c> and both launchers — and, being a relative path with nothing anchoring it, it
    /// resolved against the CURRENT DIRECTORY. Launching the executable from anywhere but its own folder failed to
    /// find the model and refused to start, naming a path that did not exist. The install script writes here, the
    /// app reads here, and there is nothing left to keep in step.</para>
    /// </summary>
    public static string ArtifactsDirectory => Path.Combine(AppContext.BaseDirectory, "tagmodel", "artifacts");

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
        var bundle = TagModelBundle.Load(ArtifactsDirectory);

        services.AddSingleton(bundle);
        services.AddSingleton(bundle.Vocab);
        services.AddSingleton<ITagCatalog>(_ => new VocabTagCatalog(bundle.Vocab));
        services.AddSingleton<ITagModelClient>(_ => new OnnxTagModelClient(bundle));
        return services;
    }
}
