using ImageGen.Comfy;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// On-demand end-to-end smoke test against a LIVE ComfyUI: builds the real graph for one or more configuration ids
/// (via the actual workflow classes + catalog), submits it to <c>/prompt</c>, and polls <c>/history</c> for a
/// produced image. Skips unless <c>IMAGEGEN_SMOKE</c> names the config ids to run, so normal CI never hits the network.
/// Run e.g.:  IMAGEGEN_SMOKE=chroma1-hd,qwen-image,hidream-fast dotnet test --filter LiveComfy
/// </summary>
public sealed class LiveComfySmokeTests
{
    [SkippableFact]
    public async Task LiveComfy_generates_for_configured_ids()
    {
        string[] ids = (Environment.GetEnvironmentVariable("IMAGEGEN_SMOKE") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Skip.If(ids.Length == 0, "set IMAGEGEN_SMOKE to the configuration ids to exercise against live ComfyUI");

        string baseUrl = Environment.GetEnvironmentVariable("COMFY_URL") ?? "http://localhost:8188";

        string root = RepoRoot();
        WorkflowCatalog catalog = new(new ComfyOptions
        {
            CatalogPath = Path.Combine(root, "configurations"),
        }, NullLogger<WorkflowCatalog>.Instance);
        // Full registry via reflection (every concrete parameterless IWorkflow in the Forge assembly).
        IWorkflow[] all = [.. typeof(IWorkflow).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IWorkflow).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (IWorkflow)(Activator.CreateInstance(t) ?? throw new InvalidOperationException($"could not instantiate {t}")))];
        WorkflowRegistry registry = new(all);

        using HttpClient http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(20) };
        List<string> results = [];
        List<string> failures = [];
        foreach (string id in ids)
        {
            try
            {
                results.Add($"{id}: OK ({await RunOne(http, catalog, registry, id)})");
            }
            catch (Exception e)
            {
                failures.Add($"{id}: {e.Message}");
            }
        }

        Console.WriteLine("SMOKE RESULTS:\n  " + string.Join("\n  ", results.Concat(failures)));
        Assert.True(failures.Count == 0, "FAILURES:\n" + string.Join("\n", failures) + "\nOK:\n" + string.Join("\n", results));
    }

    private static async Task<string> RunOne(HttpClient http, WorkflowCatalog catalog, WorkflowRegistry registry, string id)
    {
        WorkflowConfiguration cfg = catalog.FindConfig(id) ?? throw new Exception("config id not found");
        IWorkflow wf = registry.Find(cfg.WorkflowName) ?? throw new Exception($"workflow '{cfg.WorkflowName}' not registered");
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParamSpec s in wf.Schema)
        {
            if (s.Default is not null)
            {
                v[s.Key] = s.Default;
            }
        }

        foreach (KeyValuePair<string, ConfigParam> kv in cfg.Params)
        {
            v[kv.Key] = kv.Value.Value;
        }
        // Edit/i2v workflows need a source frame; upload one of the existing test stills and animate it.
        string? srcName = null;
        string positive = "a photograph of a red fox sitting in a snowy pine forest at golden hour, highly detailed";
        if (wf.Kind == WorkflowKind.Edit)
        {
            srcName = await UploadSourceAsync(http);
            positive = "gentle slow camera push-in, soft natural motion";
        }

        WorkflowInputs inputs = new()
        {
            Positive = positive,
            Negative = "blurry, low quality, deformed",
            Aspect = "square",
            SourceImageName = srcName,
        };
        ComfyWorkflowGraph graph = wf.Build(v, catalog.Resolve(cfg), inputs);

        string body = JsonSerializer.Serialize(new { prompt = graph, client_id = "smoke-" + id });
        HttpResponseMessage resp = await http.PostAsync("/prompt", new StringContent(body, Encoding.UTF8, "application/json"));
        string txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception($"/prompt {(int)resp.StatusCode}: {Trim(txt)}");
        }

        using JsonDocument doc = JsonDocument.Parse(txt);
        if (doc.RootElement.TryGetProperty("node_errors", out JsonElement ne) && ne.ValueKind == JsonValueKind.Object && ne.EnumerateObject().Any())
        {
            throw new Exception($"node_errors: {Trim(ne.GetRawText())}");
        }

        string promptId = doc.RootElement.GetProperty("prompt_id").RequireString();

        DateTime deadline = DateTime.UtcNow.AddMinutes(18);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2500);
            string h = await http.GetStringAsync($"/history/{promptId}");
            using JsonDocument hd = JsonDocument.Parse(h);
            if (!hd.RootElement.TryGetProperty(promptId, out JsonElement entry))
            {
                continue;
            }

            int images = 0;
            if (entry.TryGetProperty("outputs", out JsonElement outs))
            {
                foreach (JsonProperty node in outs.EnumerateObject())
                {
                    if (node.Value.TryGetProperty("images", out JsonElement imgs) && imgs.ValueKind == JsonValueKind.Array)
                    {
                        images += imgs.GetArrayLength();
                    }
                }
            }

            if (entry.TryGetProperty("status", out JsonElement st) && st.TryGetProperty("status_str", out JsonElement ss) && ss.GetString() == "error")
            {
                throw new Exception($"comfy error: {Trim(entry.GetRawText())}");
            }

            if (images > 0)
            {
                return $"{images} image(s)";
            }

            if (entry.TryGetProperty("status", out JsonElement st2) && st2.TryGetProperty("completed", out JsonElement c) && c.ValueKind == JsonValueKind.True)
            {
                throw new Exception("completed with no image output");
            }
        }

        throw new Exception("timeout (>18m) waiting for image");
    }

    /// <summary>Upload an existing ComfyUI output still as the i2v source frame; returns the input-folder name to reference.</summary>
    private static async Task<string> UploadSourceAsync(HttpClient http)
    {
        // Env-only: a hardcoded absolute path would fail on any other box with a confusing "directory not found"
        // instead of saying what to set.
        string dir = Environment.GetEnvironmentVariable("COMFY_OUTPUT")
            ?? throw new InvalidOperationException(
                "set COMFY_OUTPUT to your ComfyUI output directory to run the live smoke tests.");
        FileInfo png = new DirectoryInfo(dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            ?? throw new Exception($"no source .png found in {dir} for i2v test");
        byte[] bytes = await File.ReadAllBytesAsync(png.FullName);
        using MultipartFormDataContent form = new();
        ByteArrayContent img = new(bytes);
        img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(img, "image", "smoke_src.png");
        form.Add(new StringContent("true"), "overwrite");
        HttpResponseMessage r = await http.PostAsync("/upload/image", form);
        string t = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode)
        {
            throw new Exception($"/upload/image {(int)r.StatusCode}: {Trim(t)}");
        }

        using JsonDocument d = JsonDocument.Parse(t);
        string name = d.RootElement.GetProperty("name").RequireString();
        string? sub = d.RootElement.TryGetProperty("subfolder", out JsonElement s) ? s.GetString() : "";
        return string.IsNullOrEmpty(sub) ? name : $"{sub}/{name}";
    }

    private static string Trim(string s) => s.Length > 600 ? s[..600] : s;

    private static string RepoRoot()
    {
        string? d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "workflows.json")))
        {
            d = Path.GetDirectoryName(d);
        }

        return d ?? throw new DirectoryNotFoundException("workflows.json not found above test bin dir.");
    }
}
