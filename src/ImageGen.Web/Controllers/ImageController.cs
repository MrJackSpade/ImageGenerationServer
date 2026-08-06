using ImageGen.Application.Services;
using ImageGen.Application.Tags;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

[Authorize]
public sealed class ImageController(
    HistoryService history, BookmarkService bookmarks, BanService bans, ITagCatalog tags, ImageViewService views) : Controller
{
    private readonly HistoryService _history = history;
    private readonly BookmarkService _bookmarks = bookmarks;
    private readonly BanService _bans = bans;
    private readonly ITagCatalog _tags = tags;
    private readonly ImageViewService _views = views;

    [HttpGet("/image/{id}")]
    public async Task<IActionResult> Detail(string id, CancellationToken ct)
    {
        ImageDetailViewModel? vm = await BuildAsync(id, ct);
        return vm is null ? NotFound() : View(vm);
    }

    /// <summary>The image card on its own, for the in-page lightbox (see lightbox.js + _Card.cshtml).</summary>
    [HttpGet("/image/{id}/card")]
    public async Task<IActionResult> Card(string id, CancellationToken ct)
    {
        ImageDetailViewModel? vm = await BuildAsync(id, ct);
        return vm is null ? NotFound() : PartialView(Views.Card, vm);
    }

    private async Task<ImageDetailViewModel?> BuildAsync(string id, CancellationToken ct)
    {
        long userId = User.GetRequiredUserId();
        HistoryEntry? entry = await _history.GetByImageIdAsync(userId, id, ct);
        if (entry is null)
        {
            return null;
        }

        // BOTH ways of opening an image come through here — the standalone page and the lightbox's card fetch — which
        // is what "viewed" means: you looked at the picture, not that a card scrolled past you in a grid. Marked after
        // the ownership check above, so it can only ever record an image this user actually has.
        await _views.MarkViewedAsync(userId, entry.GatewayImageId, ct);

        (string? newer, string? older) = await _history.GetNeighborsAsync(userId, id, ct);
        bool isBookmarked = await _bookmarks.IsImageBookmarkedAsync(userId, id, ct);
        IReadOnlyList<BannedToken> bannedForModel = await _bans.GetForModelAsync(userId, entry.ModelId, ct);
        IReadOnlyList<TokenBookmark> tokens = await _bookmarks.GetTokensAsync(userId, ct);

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

    /// <summary>View names this controller renders.</summary>
    private static class Views
    {
        /// <summary>The image card partial, used by the in-page lightbox.</summary>
        public const string Card = "_Card";
    }
}
