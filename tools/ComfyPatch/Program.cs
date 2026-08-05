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

    /// <summary>Short help flag.</summary>
    private const string HelpFlagShort = "-h";

    /// <summary>Long help flag.</summary>
    private const string HelpFlagLong = "--help";

    /// <summary>Bare help subcommand.</summary>
    private const string HelpCommand = "help";

    /// <summary>The <c>list</c> subcommand: print every patch and its state.</summary>
    private const string ListCommand = "list";

    /// <summary>The <c>apply</c> subcommand: install the named patches.</summary>
    private const string ApplyCommand = "apply";

    /// <summary>The <c>remove</c> subcommand: uninstall the named patches.</summary>
    private const string RemoveCommand = "remove";

    /// <summary>Prefix that marks a token as an option rather than a value.</summary>
    private const string OptionPrefix = "--";

    /// <summary><c>--root</c>: the ComfyUI installation to act on.</summary>
    private const string RootOption = "root";

    /// <summary><c>--patches</c>: the comfy-patches/ directory.</summary>
    private const string PatchesOption = "patches";

    /// <summary><c>--nodes</c>: the comfy-nodes/ directory.</summary>
    private const string NodesOption = "nodes";

    /// <summary><c>--python</c>: the interpreter used to install a fetched pack's requirements.</summary>
    private const string PythonOption = "python";

    /// <summary><c>--overwrite</c>: replace files a patch installs that already hold something else.</summary>
    private const string OverwriteOption = "overwrite";

    /// <summary><c>--all</c>: select every patch, in order.</summary>
    private const string AllOption = "all";

    /// <summary><c>--id</c>: select one patch; repeatable.</summary>
    private const string IdOption = "id";

    /// <summary>ComfyUI's package directory, a marker that <c>--root</c> points at an installation.</summary>
    private const string ComfyDirMarker = "comfy";

    /// <summary>ComfyUI's entry script, the other installation marker.</summary>
    private const string MainPyMarker = "main.py";

    /// <summary>Default folder name of the patches payload.</summary>
    private const string PatchesDirName = "comfy-patches";

    /// <summary>Default folder name of the nodes payload.</summary>
    private const string NodesDirName = "comfy-nodes";

    /// <summary>Separator joining unknown patch ids in an error.</summary>
    private const string IdSeparator = ", ";

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
        if (args.Length == 0 || args[0] is HelpFlagShort or HelpFlagLong or HelpCommand)
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0];
        var options = ParseOptions(args[1..]);

        var root = Single(options, RootOption) ?? throw new ArgumentException("--root is required.");
        if (!Directory.Exists(Path.Combine(root, ComfyDirMarker)) || !File.Exists(Path.Combine(root, MainPyMarker)))
            throw new ArgumentException($"{root} is not a ComfyUI installation (no main.py and comfy/).");

        var patchDirectory = Single(options, PatchesOption) ?? Locate(PatchesDirName);
        var nodesDirectory = Single(options, NodesOption) ?? Locate(NodesDirName);
        var python = Single(options, PythonOption);

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
            case ListCommand:
                foreach (var patch in catalog)
                {
                    var (state, detail) = ComfyPatchCatalog.Inspect(patch, root);
                    Console.WriteLine($"{state,-14} {patch.Id,-32} {patch.Title}{(detail is null ? "" : "  — " + detail)}");
                }
                return catalog.Any(p => ComfyPatchCatalog.Inspect(p, root).State == PatchState.Conflicted) ? 2 : 0;

            case ApplyCommand:
            {
                foreach (var patch in Selected(catalog, options))
                {
                    var (state, _) = ComfyPatchCatalog.Inspect(patch, root);
                    if (state == PatchState.Applied)
                    {
                        Console.WriteLine($"already applied  {patch.Id}");
                        continue;
                    }

                    var note = await installer.ApplyAsync(patch, root, python, options.ContainsKey(OverwriteOption), CancellationToken.None);
                    Console.WriteLine($"applied          {patch.Id}");
                    if (note is not null) Console.WriteLine($"                 NOTE: {note}");
                }
                return 0;
            }

            case RemoveCommand:
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
        if (options.ContainsKey(AllOption)) return catalog;

        if (!options.TryGetValue(IdOption, out var ids) || ids.Count == 0)
            throw new ArgumentException("Name what to act on: --all, or --id <id>.");

        var unknown = ids.Where(id => catalog.All(p => p.Id != id)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"No such patch: {string.Join(IdSeparator, unknown)}. Run 'list' to see the ids.");

        return catalog.Where(p => ids.Contains(p.Id));
    }

    /// <summary><c>--key value</c> pairs and bare <c>--flag</c>s; a key may repeat.</summary>
    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith(OptionPrefix, StringComparison.Ordinal))
                throw new ArgumentException($"'{args[i]}' is not an option. Try --help.");

            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith(OptionPrefix, StringComparison.Ordinal) ? args[++i] : null;

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
