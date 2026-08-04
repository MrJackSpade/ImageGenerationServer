//TODO: CHECK FOR FALLBACKS
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
        var path = Path.Combine(_env.WebRootPath, "help.md");
        var markdown = System.IO.File.Exists(path)
            ? await System.IO.File.ReadAllTextAsync(path, ct)
            : "# Help\n\nHelp content isn't available.";
        return View(HelpMarkdown.Parse(markdown));
    }
}
