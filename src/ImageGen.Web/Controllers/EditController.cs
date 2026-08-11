using ImageGen.Application.Images;
using ImageGen.Application.Services;
using ImageGen.Domain.Entities;
using ImageGen.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

[Authorize]
public sealed class EditController(HistoryService history, ImageVisibilityService visibility) : Controller
{
    private readonly HistoryService _history = history;
    private readonly ImageVisibilityService _visibility = visibility;

    /// <summary>No id: the rail's Edit button lands here with no source — the page shows a file picker in the
    /// image area, and picking a file uploads it and makes it the source for every mode.</summary>
    [HttpGet("/edit")]
    public IActionResult New() =>
        View(Views.Index, new EditViewModel { ImageId = string.Empty, InitialPrompt = string.Empty, InitialTagPrompt = string.Empty });

    [HttpGet("/edit/{id}")]
    public async Task<IActionResult> Index(string id, CancellationToken ct)
    {
        // Seed the first conversation bubble with the source image's prompt when it's one of ours, and the inpaint box
        // with the prompt VERBATIM as it was submitted — markers and underscores intact, because that is the string
        // that was stored, not a reconstruction of it. Same string the card's copy button and its Reload use.
        string prompt = "(image)";
        string tagPrompt = "";
        string negativePrompt = "";
        long userId = User.GetRequiredUserId();

        // A missing history row is legitimate — a freshly uploaded source has none — but that is now distinguishable
        // from another user's image, because an upload carries its owner. Only the former loads.
        if (await _visibility.CanReadImageAsync(userId, id, ct) is null)
        {
            return Unauthorized();
        }

        HistoryEntry? entry = await _history.GetByImageIdAsync(userId, id, ct);
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

    /// <summary>View names this controller renders.</summary>
    private static class Views
    {
        /// <summary>The edit page.</summary>
        public const string Index = "Index";
    }
}
