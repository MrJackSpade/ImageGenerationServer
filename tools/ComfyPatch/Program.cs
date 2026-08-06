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
    private static readonly string Usage = """
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
        if (args.Length == 0 || args[0] is Flags.HelpFlagShort or Flags.HelpFlagLong or Commands.HelpCommand)
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0];
        Dictionary<string, List<string>> options = ParseOptions(args[1..]);

        string root = Single(options, Options.RootOption) ?? throw new ArgumentException("--root is required.");
        if (!Directory.Exists(Path.Combine(root, Markers.ComfyDirMarker)) || !File.Exists(Path.Combine(root, Markers.MainPyMarker)))
        {
            throw new ArgumentException($"{root} is not a ComfyUI installation (no main.py and comfy/).");
        }

        string? patchDirectory = Single(options, Options.PatchesOption) ?? Locate(DirNames.PatchesDirName);
        string? nodesDirectory = Single(options, Options.NodesOption) ?? Locate(DirNames.NodesDirName);
        string? python = Single(options, Options.PythonOption);

        IReadOnlyList<ComfyPatch> catalog = ComfyPatchCatalog.Load(patchDirectory, nodesDirectory);
        if (catalog.Count == 0)
        {
            throw new InvalidOperationException(
                $"No patches found. Looked in '{patchDirectory ?? "(nowhere)"}' and '{nodesDirectory ?? "(nowhere)"}'.");
        }

        using ServiceProvider services = new ServiceCollection()
            .AddHttpClient()
            .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information))
            .AddSingleton<PackSource>()
            .AddSingleton<PatchInstaller>()
            .BuildServiceProvider();

        PatchInstaller installer = services.GetRequiredService<PatchInstaller>();

        switch (command)
        {
            case Commands.ListCommand:
                foreach (ComfyPatch patch in catalog)
                {
                    (PatchState state, string? detail) = ComfyPatchCatalog.Inspect(patch, root);
                    Console.WriteLine($"{state,-14} {patch.Id,-32} {patch.Title}{(detail is null ? "" : "  — " + detail)}");
                }

                return catalog.Any(p => ComfyPatchCatalog.Inspect(p, root).State == PatchState.Conflicted) ? 2 : 0;

            case Commands.ApplyCommand:
                {
                    foreach (ComfyPatch patch in Selected(catalog, options))
                    {
                        (PatchState state, string? _) = ComfyPatchCatalog.Inspect(patch, root);
                        if (state == PatchState.Applied)
                        {
                            Console.WriteLine($"already applied  {patch.Id}");
                            continue;
                        }

                        string? note = await installer.ApplyAsync(patch, root, python, options.ContainsKey(Options.OverwriteOption), CancellationToken.None);
                        Console.WriteLine($"applied          {patch.Id}");
                        if (note is not null)
                        {
                            Console.WriteLine($"                 NOTE: {note}");
                        }
                    }

                    return 0;
                }

            case Commands.RemoveCommand:
                {
                    foreach (ComfyPatch patch in Selected(catalog, options))
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
        if (options.ContainsKey(Options.AllOption))
        {
            return catalog;
        }

        if (!options.TryGetValue(Options.IdOption, out List<string>? ids) || ids.Count == 0)
        {
            throw new ArgumentException("Name what to act on: --all, or --id <id>.");
        }

        List<string> unknown = [.. ids.Where(id => catalog.All(p => p.Id != id))];
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"No such patch: {string.Join(Format.IdSeparator, unknown)}. Run 'list' to see the ids.");
        }

        return catalog.Where(p => ids.Contains(p.Id));
    }

    /// <summary><c>--key value</c> pairs and bare <c>--flag</c>s; a key may repeat.</summary>
    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        Dictionary<string, List<string>> options = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith(Options.OptionPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{args[i]}' is not an option. Try --help.");
            }

            string key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith(Options.OptionPrefix, StringComparison.Ordinal) ? args[++i] : null;

            if (!options.TryGetValue(key, out List<string>? values))
            {
                options[key] = values = [];
            }

            if (value is not null)
            {
                values.Add(value);
            }
        }

        return options;
    }

    private static string? Single(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out List<string>? values) && values.Count > 0 ? values[^1] : null;

    /// <summary>
    /// Where a payload directory is when nobody said: beside the executable — which is how it is laid out in the
    /// container and in a release — and otherwise the working directory, which is how it is laid out in a checkout.
    /// </summary>
    private static string? Locate(string name)
    {
        foreach (string? candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, name),
                     Path.Combine(Directory.GetCurrentDirectory(), name),
                 })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Help flags.</summary>
file static class Flags
{
    /// <summary>Short help flag.</summary>
    public const string HelpFlagShort = "-h";

    /// <summary>Long help flag.</summary>
    public const string HelpFlagLong = "--help";
}

/// <summary>Subcommands.</summary>
file static class Commands
{
    /// <summary>Bare help subcommand.</summary>
    public const string HelpCommand = "help";

    /// <summary>The <c>list</c> subcommand: print every patch and its state.</summary>
    public const string ListCommand = "list";

    /// <summary>The <c>apply</c> subcommand: install the named patches.</summary>
    public const string ApplyCommand = "apply";

    /// <summary>The <c>remove</c> subcommand: uninstall the named patches.</summary>
    public const string RemoveCommand = "remove";
}

/// <summary>Command-line options.</summary>
file static class Options
{
    /// <summary>Prefix that marks a token as an option rather than a value.</summary>
    public const string OptionPrefix = "--";

    /// <summary><c>--root</c>: the ComfyUI installation to act on.</summary>
    public const string RootOption = "root";

    /// <summary><c>--patches</c>: the comfy-patches/ directory.</summary>
    public const string PatchesOption = "patches";

    /// <summary><c>--nodes</c>: the comfy-nodes/ directory.</summary>
    public const string NodesOption = "nodes";

    /// <summary><c>--python</c>: the interpreter used to install a fetched pack's requirements.</summary>
    public const string PythonOption = "python";

    /// <summary><c>--overwrite</c>: replace files a patch installs that already hold something else.</summary>
    public const string OverwriteOption = "overwrite";

    /// <summary><c>--all</c>: select every patch, in order.</summary>
    public const string AllOption = "all";

    /// <summary><c>--id</c>: select one patch; repeatable.</summary>
    public const string IdOption = "id";
}

/// <summary>ComfyUI installation markers.</summary>
file static class Markers
{
    /// <summary>ComfyUI's package directory, a marker that <c>--root</c> points at an installation.</summary>
    public const string ComfyDirMarker = "comfy";

    /// <summary>ComfyUI's entry script, the other installation marker.</summary>
    public const string MainPyMarker = "main.py";
}

/// <summary>Default payload directory names.</summary>
file static class DirNames
{
    /// <summary>Default folder name of the patches payload.</summary>
    public const string PatchesDirName = "comfy-patches";

    /// <summary>Default folder name of the nodes payload.</summary>
    public const string NodesDirName = "comfy-nodes";
}

/// <summary>Formatting literals.</summary>
file static class Format
{
    /// <summary>Separator joining unknown patch ids in an error.</summary>
    public const string IdSeparator = ", ";
}