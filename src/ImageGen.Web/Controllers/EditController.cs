using ImageGen.Application.Services;
using ImageGen.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

[Authorize]
public sealed class EditController(HistoryService history) : Controller
{
    private readonly HistoryService _history = history;

    /// <summary>No id: the rail's Edit button lands here with no source — the page shows a file picker in the
    /// image area, and picking a file uploads it and makes it the source for every mode.</summary>
    [HttpGet("/edit")]
    public IActionResult New() =>
        View("Index", new EditViewModel { ImageId = "", InitialPrompt = "", InitialTagPrompt = "" });

    [HttpGet("/edit/{id}")]
    public async Task<IActionResult> Index(string id, CancellationToken ct)
    {
        // Seed the first conversation bubble with the source image's prompt when it's one of ours, and the inpaint box
        // with the prompt VERBATIM as it was submitted — markers and underscores intact, because that is the string
        // that was stored, not a reconstruction of it. Same string the card's copy button and its Reload use.
        var prompt = "(image)";
        var tagPrompt = "";
        var negativePrompt = "";
        var userId = User.GetUserId()!.Value;
        var entry = await _history.GetByImageIdAsync(userId, id, ct);
        if (entry is not null)
        {
            prompt = entry.Prompt;
            tagPrompt = entry.RawPrompt ?? "";
            negativePrompt = entry.RawNegativePrompt ?? "";
        }
        return View(new EditViewModel
        {
            ImageId = id,
            InitialPrompt = prompt,
            InitialTagPrompt = tagPrompt,
            InitialNegativePrompt = negativePrompt,
        });
    }
}
