using ImageGen.Application.Images;
using ImageGen.Application.Services;
using ImageGen.Application.Tags;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

[Authorize]
public sealed class ImageController(
    HistoryService history, BookmarkService bookmarks, BanService bans, ITagCatalog tags, ImageViewService views,
    ImageVisibilityService visibility) : Controller
{
    private readonly HistoryService _history = history;
    private readonly BookmarkService _bookmarks = bookmarks;
    private readonly BanService _bans = bans;
    private readonly ITagCatalog _tags = tags;
    private readonly ImageViewService _views = views;
    private readonly ImageVisibilityService _visibility = visibility;

    [HttpGet("/image/{id}")]
    public async Task<IActionResult> Detail(string id, CancellationToken ct)
    {
        long userId = User.GetRequiredUserId();
        Task<ImageReadGrant?> visibilityTask = _visibility.CanReadImageAsync(userId, id, ct);
        Task<ImageDetailViewModel?> detailTask = BuildAsync(userId, id, ct);
        await Task.WhenAll(visibilityTask, detailTask);

        if (await visibilityTask is null)
        {
            return Unauthorized();
        }

        ImageDetailViewModel? vm = await detailTask;
        if (vm is null)
        {
            return NotFound();
        }

        // A full detail-page navigation is itself a view. The JSON endpoint below is deliberately side-effect-free:
        // the lightbox records this same fact through its explicit POST only after it has rendered the image.
        await _views.MarkViewedAsync(userId, vm.Entry.GatewayImageId, ct);
        return View(vm);
    }

    /// <summary>Presentation data for the in-page lightbox. JSON only and side-effect-free: merely preloading or
    /// inspecting detail data is not a view.</summary>
    [HttpGet("/image/{id}/detail")]
    public async Task<IActionResult> DetailData(string id, CancellationToken ct)
    {
        long userId = User.GetRequiredUserId();
        Task<ImageReadGrant?> visibilityTask = _visibility.CanReadImageAsync(userId, id, ct);
        Task<ImageDetailViewModel?> detailTask = BuildAsync(userId, id, ct);
        await Task.WhenAll(visibilityTask, detailTask);

        if (await visibilityTask is null)
        {
            return Unauthorized();
        }

        ImageDetailViewModel? vm = await detailTask;
        return vm is null ? NotFound() : Json(vm.ToRecord());
    }

    /// <summary>Record the intentional act of opening an image. Kept separate from detail-data delivery so a JSON
    /// fetch or future prefetch cannot silently clear the unviewed state.</summary>
    [HttpPost("/image/{id}/view")]
    public async Task<IActionResult> MarkViewed(string id, CancellationToken ct)
    {
        long userId = User.GetRequiredUserId();
        if (await _visibility.CanReadImageAsync(userId, id, ct) is null)
        {
            return Unauthorized();
        }

        HistoryEntry? entry = await _history.GetByImageIdAsync(userId, id, ct);
        if (entry is null)
        {
            return NotFound();
        }

        await _views.MarkViewedAsync(userId, entry.GatewayImageId, ct);
        return NoContent();
    }

    /// <summary>The page's data, or null when the caller has no history row for the id — a readable id that never
    /// entered their history (its slot produced it, the history write did not land) has no detail to show.</summary>
    private async Task<ImageDetailViewModel?> BuildAsync(long userId, string id, CancellationToken ct)
    {
        HistoryEntry? entry = await _history.GetByImageIdAsync(userId, id, ct);
        if (entry is null)
        {
            return null;
        }

        Task<HistoryNeighbors> neighborsTask = _history.GetNeighborsAsync(userId, id, ct);
        Task<bool> isBookmarkedTask = _bookmarks.IsImageBookmarkedAsync(userId, id, ct);
        Task<IReadOnlyList<BannedToken>> bannedForModelTask = _bans.GetForModelAsync(userId, entry.ModelId, ct);
        Task<IReadOnlyList<TokenBookmark>> tokensTask = _bookmarks.GetTokensAsync(userId, ct);
        await Task.WhenAll(neighborsTask, isBookmarkedTask, bannedForModelTask, tokensTask);

        (string? newer, string? older) = await neighborsTask;
        bool isBookmarked = await isBookmarkedTask;
        IReadOnlyList<BannedToken> bannedForModel = await bannedForModelTask;
        IReadOnlyList<TokenBookmark> tokens = await tokensTask;

        // Look up each tag token's booru category: it colors the chip border and orders the chips by type. Artists are
        // colored and ranked by kind, so skip them; tags the catalog doesn't know stay absent and count as general.
        Dictionary<string, int> tagTypeByToken = new(StringComparer.Ordinal);
        foreach (Mark m in entry.Marks)
        {
            if (m.Kind != TokenKind.Tag)
            {
                continue;
            }

            if (_tags.Lookup(m.Token) is { } t)
            {
                tagTypeByToken[m.Token] = t.Type;
            }
        }

        return new ImageDetailViewModel
        {
            Entry = entry.ToDetailView(),
            TagTypeByToken = tagTypeByToken,
            MarkerPrompt = entry.RawPrompt,                // stored verbatim at render time; loaded as-is (null = pre-column), never rebuilt
            MarkerNegativePrompt = entry.RawNegativePrompt,   // null = none submitted; NOT the same as ""
            OriginalPrompt = entry.OriginalPrompt,            // null = never recorded (pre-column, unbackfillable)
            IsBookmarked = isBookmarked,
            NewerId = newer,
            OlderId = older,
            BannedTags = bannedForModel.Where(b => b.Kind == TokenKind.Tag).Select(b => b.Name).ToHashSet(StringComparer.Ordinal),
            BannedArtists = bannedForModel.Where(b => b.Kind == TokenKind.Artist).Select(b => b.Name).ToHashSet(StringComparer.Ordinal),
            BookmarkedTags = tokens.Where(t => t.Kind == TokenKind.Tag).Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
            BookmarkedArtists = tokens.Where(t => t.Kind == TokenKind.Artist).Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
        };
    }
}
