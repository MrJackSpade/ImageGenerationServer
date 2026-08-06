using ImageGen.Application.Media;
using ImageGen.Application.Rendering;
using ImageGen.Application.Tags;
using ImageGen.Application.Workflows;

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
            sp.GetRequiredService<ILogger<ComfyClient>>()));
        _ = services.AddSingleton<IComfyClient>(sp => sp.GetRequiredService<ComfyClient>());
        _ = services.AddSingleton<IWorkflowCatalog, WorkflowCatalogService>();
        // Background filler of the LoRA CivitAI cache (hash → look up → cache preview). One instance is both the queue
        // surfaces post to (ILoraMetaPopulator) and the hosted worker that drains it, so registrations share it.
        _ = services.AddSingleton<LoraMetaPopulator>();
        _ = services.AddSingleton<ImageGen.Application.Civitai.ILoraMetaPopulator>(sp => sp.GetRequiredService<LoraMetaPopulator>());
        _ = services.AddHostedService(sp => sp.GetRequiredService<LoraMetaPopulator>());

        // ITagCatalog and ITagModelClient are NOT registered here. Both are served in-process by
        // ImageGen.TagModel over the model's own vocabulary (AddTagModel).
        return services;
    }
}
