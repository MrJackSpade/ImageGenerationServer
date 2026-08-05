using ImageGen.Application.Images;
using ImageGen.Application.Logging;
using ImageGen.Application.Rendering;
using ImageGen.Application.Security;
using ImageGen.Application.Services;
using ImageGen.Domain.Logging;
using ImageGen.Domain.Repositories;

namespace ImageGen.Application;

/// <summary>
/// Registers the application (use-case) layer: the per-request services, the prompt-presentation helper, the encrypted
/// user-log service, and the render orchestrator (a singleton; the web host adapts its <see cref="RenderOrchestrator.RunAsync"/>
/// to a hosted service). This layer registers no adapters — the ComfyUI client, tag stores, media processor, workflow
/// catalog, and repositories are supplied by <c>AddInfrastructure</c>/<c>AddComfy</c>/<c>AddMedia</c>.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Add the application layer's services and the render orchestrator.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="renderOptions">Render-pipeline options (e.g. prompt logging).</param>
    /// <param name="auditUserPrompts">When true, prompt-bearing events are written to the per-user encrypted log.</param>
    public static IServiceCollection AddApplication(
        this IServiceCollection services, RenderOptions renderOptions, bool auditUserPrompts)
    {
        services.AddScoped<UserService>();
        services.AddScoped<HistoryService>();
        services.AddScoped<BookmarkService>();
        services.AddScoped<BanService>();
        services.AddScoped<PendingJobService>();
        services.AddScoped<ArtistService>();
        services.AddScoped<LoraService>();
        services.AddScoped<TagService>();
        services.AddScoped<ImageViewService>();

        services.AddSingleton<IUserLogService>(sp => new UserLogService(
            sp.GetRequiredService<IUserCipher>(),
            sp.GetRequiredService<IUserLogRepository>(),
            auditUserPrompts,
            sp.GetRequiredService<ILogger<UserLogService>>()));

        // Uploaded sources/references/masks live here and nowhere else — never in the database, and never evicted
        // (an accepted job's inputs have to outlive its wait in the queue). See IUploadStore.
        services.AddSingleton<IUploadStore>(new InMemoryUploadStore());

        services.AddSingleton(renderOptions);
        services.AddSingleton<RenderOrchestrator>();
        return services;
    }
}
