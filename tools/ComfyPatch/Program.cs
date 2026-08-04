using ImageGen.Comfy.Patches;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ComfyPatchCli;

/// <summary>
/// Applies this build's ComfyUI patches from a command line.
///
/// <para>The container build runs <c>apply --all</c>, and a patch that will not apply fails the build. That is
/// the point: the image pins a ComfyUI release, and the alternative to failing here is shipping a container
/// that quietly lacks a fix nobody notices is gone until a render is wrong.</para>
/// </summary>
public static class Program
{
    private const string Usage = """
        ComfyPatch — apply this build's ComfyUI patches.

          ComfyPatch list   --root <comfyui>
          ComfyPatch apply  --root <comfyui> (--all | --id <id>)
          ComfyPatch remove --root <comfyui> --id <id>

        Options:
          --root <dir>      the ComfyUI installation to act on          (required)
          --patches <dir>   comfy-patches/     (default: beside this executable, then the working directory)
          --nodes <dir>     comfy-nodes/       (default: as above)
          --python <exe>    the interpreter running that ComfyUI, used to install a fetched pack's requirements
          --id <id>         one patch; repeatable
          --all             every patch, in order
          --overwrite       on apply, replace files a patch installs that are already there holding
                            something else. Off by default: it discards whatever was in them.
        """;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            // The message IS the result here. A build step that fails silently, or with a stack trace where the
            // reason should be, is worse than one that does not run at all.
            Console.Error.WriteLine($"ComfyPatch: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0];
        var options = ParseOptions(args[1..]);

        var root = Single(options, "root") ?? throw new ArgumentException("--root is required.");
        if (!Directory.Exists(Path.Combine(root, "comfy")) || !File.Exists(Path.Combine(root, "main.py")))
            throw new ArgumentException($"{root} is not a ComfyUI installation (no main.py and comfy/).");

        var patchDirectory = Single(options, "patches") ?? Locate("comfy-patches");
        var nodesDirectory = Single(options, "nodes") ?? Locate("comfy-nodes");
        var python = Single(options, "python");

        var catalog = ComfyPatchCatalog.Load(patchDirectory, nodesDirectory);
        if (catalog.Count == 0)
            throw new InvalidOperationException(
                $"No patches found. Looked in '{patchDirectory ?? "(nowhere)"}' and '{nodesDirectory ?? "(nowhere)"}'.");

        using var services = new ServiceCollection()
            .AddHttpClient()
            .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information))
            .AddSingleton<PackSource>()
            .AddSingleton<PatchInstaller>()
            .BuildServiceProvider();

        var installer = services.GetRequiredService<PatchInstaller>();

        switch (command)
        {
            case "list":
                foreach (var patch in catalog)
                {
                    var (state, detail) = ComfyPatchCatalog.Inspect(patch, root);
                    Console.WriteLine($"{state,-14} {patch.Id,-32} {patch.Title}{(detail is null ? "" : "  — " + detail)}");
                }
                return catalog.Any(p => ComfyPatchCatalog.Inspect(p, root).State == PatchState.Conflicted) ? 2 : 0;

            case "apply":
            {
                foreach (var patch in Selected(catalog, options))
                {
                    var (state, _) = ComfyPatchCatalog.Inspect(patch, root);
                    if (state == PatchState.Applied)
                    {
                        Console.WriteLine($"already applied  {patch.Id}");
                        continue;
                    }

                    var note = await installer.ApplyAsync(patch, root, python, options.ContainsKey("overwrite"), CancellationToken.None);
                    Console.WriteLine($"applied          {patch.Id}");
                    if (note is not null) Console.WriteLine($"                 NOTE: {note}");
                }
                return 0;
            }

            case "remove":
            {
                foreach (var patch in Selected(catalog, options))
                {
                    installer.Remove(patch, root);
                    Console.WriteLine($"removed          {patch.Id}");
                }
                return 0;
            }

            default:
                throw new ArgumentException($"'{command}' is not a command. Try --help.");
        }
    }

    /// <summary>The patches this invocation names, in apply order.</summary>
    private static IEnumerable<ComfyPatch> Selected(IReadOnlyList<ComfyPatch> catalog, Dictionary<string, List<string>> options)
    {
        if (options.ContainsKey("all")) return catalog;

        if (!options.TryGetValue("id", out var ids) || ids.Count == 0)
            throw new ArgumentException("Name what to act on: --all, or --id <id>.");

        var unknown = ids.Where(id => catalog.All(p => p.Id != id)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"No such patch: {string.Join(", ", unknown)}. Run 'list' to see the ids.");

        return catalog.Where(p => ids.Contains(p.Id));
    }

    /// <summary><c>--key value</c> pairs and bare <c>--flag</c>s; a key may repeat.</summary>
    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"'{args[i]}' is not an option. Try --help.");

            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;

            if (!options.TryGetValue(key, out var values)) options[key] = values = [];
            if (value is not null) values.Add(value);
        }
        return options;
    }

    private static string? Single(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values) && values.Count > 0 ? values[^1] : null;

    /// <summary>
    /// Where a payload directory is when nobody said: beside the executable — which is how it is laid out in the
    /// container and in a release — and otherwise the working directory, which is how it is laid out in a checkout.
    /// </summary>
    private static string? Locate(string name)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, name),
                     Path.Combine(Directory.GetCurrentDirectory(), name),
                 })
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
