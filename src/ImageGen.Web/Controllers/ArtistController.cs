using ImageGen.Application.Services;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

[Authorize]
public sealed class ArtistController(ArtistService artists, ImageViewService views) : Controller
{
    private const int PageSize = 40;
    private readonly ArtistService _artists = artists;
    private readonly ImageViewService _views = views;

    /// <summary>
    /// The artist page: a hero display image (override or latest gen) plus all the user's generations for the
    /// artist. First page only; artist.js loads the rest via /api/history?artist= as you scroll.
    /// </summary>
    [HttpGet("/artist/{name}")]
    public async Task<IActionResult> Index(string name, CancellationToken ct)
    {
        long userId = User.GetRequiredUserId();
        (string? displayId, bool isOverride) = await _artists.GetDisplayAsync(userId, name, ct);
        PagedResult<HistoryEntry> gens = await _artists.GetGensAsync(userId, name, 1, PageSize, ct);
        IReadOnlySet<string> viewed = await _views.ViewedAsync(userId, gens.Items, ct);
        return View(new ArtistViewModel
        {
            Name = name,
            DisplayImageId = displayId,
            HasOverride = isOverride,
            Items = gens.Items.Select(e => e.ToItemView(viewed)).ToList(),
            Page = gens.Page,
            PageSize = gens.PageSize,
            Total = gens.Total,
        });
    }
}
