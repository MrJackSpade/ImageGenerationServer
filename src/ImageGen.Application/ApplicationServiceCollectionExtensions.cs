using ImageGen.Application.Images;
using ImageGen.Application.Logging;
using ImageGen.Application.Rendering;
using ImageGen.Application.Security;
using ImageGen.Application.Services;
using ImageGen.Application.Snapshots;
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
        _ = services.AddScoped<UserService>();
        _ = services.AddScoped<HistoryService>();
        _ = services.AddScoped<BookmarkService>();
        _ = services.AddScoped<BanService>();
        _ = services.AddScoped<PendingJobService>();
        _ = services.AddScoped<ArtistService>();
        _ = services.AddScoped<LoraService>();
        _ = services.AddScoped<TagService>();
        _ = services.AddScoped<ImageViewService>();

        _ = services.AddSingleton<IUserLogService>(sp => new UserLogService(
            sp.GetRequiredService<IUserCipher>(),
            sp.GetRequiredService<IUserLogRepository>(),
            auditUserPrompts,
            sp.GetRequiredService<ILogger<UserLogService>>()));

        // Uploaded sources/references/masks live here and nowhere else — never in the database, and never evicted
        // (an accepted job's inputs have to outlive its wait in the queue). See IUploadStore.
        _ = services.AddSingleton<IUploadStore>(new InMemoryUploadStore());

        _ = services.AddSingleton(renderOptions);
        _ = services.AddSingleton<RenderOrchestrator>();

        // Per-model recent-average render timings (#200): a machine-scoped SQL read that used to run live inside both
        // /forge/workflows and the ~2s-polled /forge/queue. Flushed on job finalization (RenderOrchestrator), backstop
        // 5 minutes. The window (10) matches the pre-snapshot query. Registered here because the orchestrator that
        // flushes it lives here; the loader resolves the singleton repository from the root provider.
        _ = services.AddSnapshot(
            static async (sp, ct) => new GenTimingAverages(
                await sp.GetRequiredService<IGenTimingRepository>().RecentAveragesMsAsync(Environment.MachineName, 10, ct)),
            new SnapshotOptions { BackstopInterval = TimeSpan.FromMinutes(5) });

        return services;
    }
}
