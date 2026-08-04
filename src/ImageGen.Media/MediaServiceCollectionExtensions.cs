//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Media;
using Microsoft.Extensions.DependencyInjection;

namespace ImageGen.Media;

/// <summary>Registers the media adapter (<see cref="IMediaProcessor"/> over ImageSharp + ffmpeg).</summary>
public static class MediaServiceCollectionExtensions
{
    /// <summary>Add the media processor with the given options.</summary>
    public static IServiceCollection AddMedia(this IServiceCollection services, MediaOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IMediaProcessor, MediaProcessor>();
        return services;
    }
}
