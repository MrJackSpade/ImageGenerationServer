using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ImageGen.Comfy;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageGen.Tests;

/// <summary>
/// On-demand end-to-end smoke test against a LIVE ComfyUI: builds the real graph for one or more configuration ids
/// (via the actual workflow classes + catalog), submits it to <c>/prompt</c>, and polls <c>/history</c> for a
/// produced image. Skips unless <c>IMAGEGEN_SMOKE</c> names the config ids to run, so normal CI never hits the network.
/// Run e.g.:  IMAGEGEN_SMOKE=chroma1-hd,qwen-image,hidream-fast dotnet test --filter LiveComfy
/// </summary>
public sealed class LiveComfySmokeTests
{
    [Fact]
    public async Task LiveComfy_generates_for_configured_ids()
    {
        var ids = (Environment.GetEnvironmentVariable("IMAGEGEN_SMOKE") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return;   // not targeted -> skip silently
        var baseUrl = Environment.GetEnvironmentVariable("COMFY_URL") ?? "http://localhost:8188";

        var root = RepoRoot();
        var catalog = new WorkflowCatalog(new ComfyOptions
        {
            CatalogPath = Path.Combine(root, "configurations"),
        }, NullLogger<WorkflowCatalog>.Instance);
        // Full registry via reflection (every concrete parameterless IWorkflow in the Forge assembly).
        var all = typeof(IWorkflow).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IWorkflow).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (IWorkflow)(Activator.CreateInstance(t) ?? throw new InvalidOperationException($"could not instantiate {t}"))).ToArray();
        var registry = new WorkflowRegistry(all);

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(20) };
        var results = new List<string>();
        var failures = new List<string>();
        foreach (var id in ids)
        {
            try { results.Add($"{id}: OK ({await RunOne(http, catalog, registry, id)})"); }
            catch (Exception e) { failures.Add($"{id}: {e.Message}"); }
        }
        Console.WriteLine("SMOKE RESULTS:\n  " + string.Join("\n  ", results.Concat(failures)));
        Assert.True(failures.Count == 0, "FAILURES:\n" + string.Join("\n", failures) + "\nOK:\n" + string.Join("\n", results));
    }

    private static async Task<string> RunOne(HttpClient http, WorkflowCatalog catalog, WorkflowRegistry registry, string id)
    {
        var cfg = catalog.FindConfig(id) ?? throw new Exception("config id not found");
        var wf = registry.Find(cfg.WorkflowName) ?? throw new Exception($"workflow '{cfg.WorkflowName}' not registered");
        var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wf.Schema) if (s.Default is not null) v[s.Key] = s.Default;
        foreach (var kv in cfg.Params) v[kv.Key] = kv.Value.Value;
        // Edit/i2v workflows need a source frame; upload one of the existing test stills and animate it.
        string? srcName = null;
        var positive = "a photograph of a red fox sitting in a snowy pine forest at golden hour, highly detailed";
        if (wf.Kind == WorkflowKind.Edit)
        {
            srcName = await UploadSourceAsync(http);
            positive = "gentle slow camera push-in, soft natural motion";
        }
        var inputs = new WorkflowInputs
        {
            Positive = positive,
            Negative = "blurry, low quality, deformed",
            Aspect = "square",
            SourceImageName = srcName,
        };
        var graph = wf.Build(new ParamValues(v), catalog.Resolve(cfg), inputs);

        var body = JsonSerializer.Serialize(new { prompt = graph, client_id = "smoke-" + id });
        var resp = await http.PostAsync("/prompt", new StringContent(body, Encoding.UTF8, "application/json"));
        var txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"/prompt {(int)resp.StatusCode}: {Trim(txt)}");
        using var doc = JsonDocument.Parse(txt);
        if (doc.RootElement.TryGetProperty("node_errors", out var ne) && ne.ValueKind == JsonValueKind.Object && ne.EnumerateObject().Any())
            throw new Exception($"node_errors: {Trim(ne.GetRawText())}");
        var promptId = doc.RootElement.GetProperty("prompt_id").RequireString();

        var deadline = DateTime.UtcNow.AddMinutes(18);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2500);
            var h = await http.GetStringAsync($"/history/{promptId}");
            using var hd = JsonDocument.Parse(h);
            if (!hd.RootElement.TryGetProperty(promptId, out var entry)) continue;
            int images = 0;
            if (entry.TryGetProperty("outputs", out var outs))
                foreach (var node in outs.EnumerateObject())
                    if (node.Value.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                        images += imgs.GetArrayLength();
            if (entry.TryGetProperty("status", out var st) && st.TryGetProperty("status_str", out var ss) && ss.GetString() == "error")
                throw new Exception($"comfy error: {Trim(entry.GetRawText())}");
            if (images > 0) return $"{images} image(s)";
            if (entry.TryGetProperty("status", out var st2) && st2.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True)
                throw new Exception("completed with no image output");
        }
        throw new Exception("timeout (>18m) waiting for image");
    }

    /// <summary>Upload an existing ComfyUI output still as the i2v source frame; returns the input-folder name to reference.</summary>
    private static async Task<string> UploadSourceAsync(HttpClient http)
    {
        // Env-only: a hardcoded absolute path would fail on any other box with a confusing "directory not found"
        // instead of saying what to set.
        var dir = Environment.GetEnvironmentVariable("COMFY_OUTPUT")
            ?? throw new InvalidOperationException(
                "set COMFY_OUTPUT to your ComfyUI output directory to run the live smoke tests.");
        var png = new DirectoryInfo(dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            ?? throw new Exception($"no source .png found in {dir} for i2v test");
        var bytes = await File.ReadAllBytesAsync(png.FullName);
        using var form = new MultipartFormDataContent();
        var img = new ByteArrayContent(bytes);
        img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(img, "image", "smoke_src.png");
        form.Add(new StringContent("true"), "overwrite");
        var r = await http.PostAsync("/upload/image", form);
        var t = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Exception($"/upload/image {(int)r.StatusCode}: {Trim(t)}");
        using var d = JsonDocument.Parse(t);
        var name = d.RootElement.GetProperty("name").RequireString();
        var sub = d.RootElement.TryGetProperty("subfolder", out var s) ? s.GetString() : "";
        return string.IsNullOrEmpty(sub) ? name : $"{sub}/{name}";
    }

    private static string Trim(string s) => s.Length > 600 ? s[..600] : s;

    private static string RepoRoot()
    {
        var d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "workflows.json"))) d = Path.GetDirectoryName(d);
        return d ?? throw new DirectoryNotFoundException("workflows.json not found above test bin dir.");
    }
}
