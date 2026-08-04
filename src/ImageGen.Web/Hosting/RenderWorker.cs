//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Rendering;

namespace ImageGen.Web.Hosting;

/// <summary>
/// Hosted-service adapter that runs the <see cref="RenderOrchestrator"/>'s background render loop. Lives in the web
/// host so the Application layer stays free of the generic host (Microsoft.Extensions.Hosting): the orchestrator is a
/// plain singleton exposing <see cref="RenderOrchestrator.RunAsync"/>, and this bridges it to a <see cref="BackgroundService"/>.
/// </summary>
public sealed class RenderWorker(RenderOrchestrator orchestrator) : BackgroundService
{
    private readonly RenderOrchestrator _orchestrator = orchestrator;

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _orchestrator.RunAsync(stoppingToken);
}
