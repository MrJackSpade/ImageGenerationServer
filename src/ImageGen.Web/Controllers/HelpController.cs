using ImageGen.Web.Help;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

[Authorize]
public sealed class HelpController(IWebHostEnvironment env) : Controller
{
    private readonly IWebHostEnvironment _env = env;

    /// <summary>Renders the help.md shipped in wwwroot (read fresh each request so a redeploy of it takes effect at once).</summary>
    [HttpGet("/help")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var path = Path.Combine(_env.WebRootPath, Files.HelpMarkdown);
        // help.md ships in wwwroot, so a missing one is a broken deployment, not a page to render a placeholder for.
        if (!System.IO.File.Exists(path))
            throw new FileNotFoundException(
                $"help.md is missing from the web root ({_env.WebRootPath}); it ships with the app.", path);
        var markdown = await System.IO.File.ReadAllTextAsync(path, ct);
        return View(HelpMarkdown.Parse(markdown));
    }

    /// <summary>Names of files this controller reads from the web root.</summary>
    private static class Files
    {
        /// <summary>The help document shipped in wwwroot.</summary>
        public const string HelpMarkdown = "help.md";
    }
}
