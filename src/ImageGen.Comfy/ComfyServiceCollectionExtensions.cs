using ImageGen.Application.Media;
using ImageGen.Application.Rendering;
using ImageGen.Application.Snapshots;
using ImageGen.Application.Tags;
using ImageGen.Application.Workflows;
using ImageGen.Comfy.Snapshots;

namespace ImageGen.Comfy;

/// <summary>
/// Registers the ComfyUI adapter: the workflow catalog + graph engine, the ComfyUI client (as <see cref="IComfyClient"/>
/// and <see cref="IWorkflowCatalog"/>), and the booru tag stores (<see cref="ITagCatalog"/> / <see cref="ITagModelClient"/>).
/// Depends on <see cref="IMediaProcessor"/> being registered (via AddMedia).
/// </summary>
public static class ComfyServiceCollectionExtensions
{
    /// <summary>Gelbooru's category id for artist tags. A fact about the tag data, not something a deployment configures.</summary>
    private const int GelbooruArtistCategory = 1;

    /// <summary>Add the ComfyUI adapter and workflow catalog for the given options.</summary>
    public static IServiceCollection AddComfy(this IServiceCollection services, ComfyOptions options)
    {
        _ = services.AddSingleton(options);
        _ = services.AddHttpClient();
        _ = services.AddWorkflows();                     // the IWorkflow graph set + WorkflowRegistry
        _ = services.AddSingleton<WorkflowCatalog>();

        // The client takes the FACTORY, not a client: the renderer's address can change while the app is running,
        // and an HttpClient's BaseAddress cannot. It builds a new one and disposes the old when the address moves.
        // IComfyEndpoint is the composition root's — this adapter does not know what a configuration key is.
        _ = services.AddSingleton<ComfyClient>(sp => new ComfyClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IComfyEndpoint>(),
            sp.GetRequiredService<WorkflowCatalog>(),
            sp.GetRequiredService<WorkflowRegistry>(),
            sp.GetRequiredService<IMediaProcessor>(),
            sp.GetRequiredService<ISnapshot<ComfyFilesByKind>>(),
            sp.GetRequiredService<ILogger<ComfyClient>>()));
        _ = services.AddSingleton<IComfyClient>(sp => sp.GetRequiredService<ComfyClient>());
        _ = services.AddSingleton<IWorkflowCatalog, WorkflowCatalogService>();

        AddComfyProbeSnapshots(services);
        // Background filler of the LoRA CivitAI cache (hash → look up → cache preview). One instance is both the queue
        // surfaces post to (ILoraMetaPopulator) and the hosted worker that drains it, so registrations share it.
        _ = services.AddSingleton<LoraMetaPopulator>();
        _ = services.AddSingleton<ImageGen.Application.Civitai.ILoraMetaPopulator>(sp => sp.GetRequiredService<LoraMetaPopulator>());
        _ = services.AddHostedService(sp => sp.GetRequiredService<LoraMetaPopulator>());

        // The ONE process-wide ComfyUI event connection: pins sampler progress on the orchestrator and publishes the
        // same text/preview stream to the owner-filtered browser fan-out. ComfyUI permits only one socket per client id,
        // so /forge/ws subscribers never open their own upstream connections.
        _ = services.AddHostedService<ComfyProgressListener>();

        // ITagCatalog and ITagModelClient are NOT registered here. Both are served in-process by
        // ImageGen.TagModel over the model's own vocabulary (AddTagModel).
        return services;
    }

    /// <summary>The owner-chosen backstop cadence for the ComfyUI capability probes and the machine SQL sources (#187
    /// discussion). The core ships no default; this is where the 5-minute value for these sources lives.</summary>
    private static readonly TimeSpan SnapshotBackstop = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Register the three ComfyUI capability-probe snapshot sources (#198), their facade, and the model-directory
    /// watcher. Each source's loader runs one live probe on the single sync worker; the flat present-files union is
    /// derived from the by-kind sweep (one pass, not two). The folder-paths rebuild re-arms the watcher over whatever
    /// roots ComfyUI reports.
    /// </summary>
    private static void AddComfyProbeSnapshots(IServiceCollection services)
    {
        _ = services.AddSingleton<ComfyModelDirectoryWatcher>();

        _ = services.AddSnapshot(
            static async (sp, ct) =>
            {
                ComfyFilesByKind files = new(await sp.GetRequiredService<ComfyClient>().GetPresentFilesByKindAsync(ct));
                // A change in the present files can create new auto-bind opportunities, so nudge the bindings source to
                // re-run its recognition pass against the new file set (#199). The cascade is one-directional — bindings
                // reads files but never re-invalidates them — so it terminates. GetService (not Required) keeps this
                // source independent of the SQL sources being registered.
                sp.GetService<ISnapshot<BindingsSnapshot>>()?.Invalidate();
                return files;
            },
            new SnapshotOptions { BackstopInterval = SnapshotBackstop });

        _ = services.AddSnapshot(
            static async (sp, ct) =>
            {
                WorkflowCatalog catalog = sp.GetRequiredService<WorkflowCatalog>();
                IEnumerable<string> nodes = catalog.AllRequirements()
                    .Where(r => !string.IsNullOrWhiteSpace(r.Node))
                    .Select(r => r.Node)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal);
                return new ComfyPresentNodes(await sp.GetRequiredService<ComfyClient>().GetPresentNodesAsync(nodes, ct));
            },
            new SnapshotOptions { BackstopInterval = SnapshotBackstop });

        _ = services.AddSnapshot(
            static async (sp, ct) =>
            {
                ComfyFolderPaths paths = new(await sp.GetRequiredService<ComfyClient>().GetFolderPathsAsync(ct));
                // Re-arm the directory watcher over the roots ComfyUI actually reports (local roots only; remote ones
                // are skipped inside Sync). Done in the rebuild so the watched set follows folder-path changes.
                sp.GetRequiredService<ComfyModelDirectoryWatcher>().Sync(paths.AllDirectories);
                return paths;
            },
            new SnapshotOptions { BackstopInterval = SnapshotBackstop });

        _ = services.AddSingleton<ComfyProbeSnapshots>();

        AddCatalogSqlSnapshots(services);
    }

    /// <summary>
    /// Register the three machine-scoped SQL snapshot sources (#199): bindings (with the relocated auto-bind
    /// recognition pass), param overrides, and variants. Each rebuild pushes into the in-memory catalog, so the
    /// synchronous submit-path resolve works without a query. Backstop 5 minutes — three tiny indexed queries.
    /// </summary>
    private static void AddCatalogSqlSnapshots(IServiceCollection services)
    {
        _ = services.AddSingleton<CatalogSqlSnapshotSources>();

        _ = services.AddSnapshot(
            static (sp, ct) => sp.GetRequiredService<CatalogSqlSnapshotSources>().LoadBindingsAsync(ct),
            new SnapshotOptions { BackstopInterval = SnapshotBackstop });

        _ = services.AddSnapshot(
            static (sp, ct) => sp.GetRequiredService<CatalogSqlSnapshotSources>().LoadOverridesAsync(ct),
            new SnapshotOptions { BackstopInterval = SnapshotBackstop });

        _ = services.AddSnapshot(
            static (sp, ct) => sp.GetRequiredService<CatalogSqlSnapshotSources>().LoadVariantsAsync(ct),
            new SnapshotOptions { BackstopInterval = SnapshotBackstop });

        _ = services.AddSingleton<CatalogSnapshots>();
    }
}
