using ImageGen.Application.Media;

namespace ImageGen.Media;

/// <summary>Registers the media adapter (<see cref="IMediaProcessor"/> over ImageSharp + ffmpeg).</summary>
public static class MediaServiceCollectionExtensions
{
    /// <summary>Add the media processor with the given options.</summary>
    public static IServiceCollection AddMedia(this IServiceCollection services, MediaOptions options)
    {
        _ = services.AddSingleton(options);
        _ = services.AddSingleton<IMediaProcessor, MediaProcessor>();
        return services;
    }
}
