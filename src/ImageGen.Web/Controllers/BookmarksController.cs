using ImageGen.Application.Services;
using ImageGen.Application.Tags;
using ImageGen.Domain;
using ImageGen.Domain.Repositories;
using ImageGen.Web.Auth;
using ImageGen.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

[Authorize]
public sealed class BookmarksController(
    BookmarkService bookmarks, HistoryService history, ArtistService artists, TagService tags,
    ITagCatalog tagCatalog, ImageViewService views) : Controller
{
    private readonly ImageViewService _views = views;
    private readonly BookmarkService _bookmarks = bookmarks;
    private readonly HistoryService _history = history;
    private readonly ArtistService _artists = artists;
    private readonly TagService _tags = tags;
    private readonly ITagCatalog _tagCatalog = tagCatalog;

    /// <summary>
    /// The images made with a starred tag. POST, so the TAG travels in the request body: as <c>/bookmarks?tag=…</c>
    /// it would go into the browser's own history and address-bar autocomplete on the user's machine, where nothing
    /// server-side can reach it, as well as into request logs, proxies and Referer headers. The trade-off is
    /// deliberate — this view is not a bookmarkable URL, because being one is exactly what would leak it.
    /// <para>An ARTIST is not protected (an artist token on its own carries nothing embarrassing), which is why
    /// /artist/{name} stays a plain, linkable GET.</para>
    /// </summary>
    [HttpPost("/bookmarks/tag")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Tag(string tag, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return RedirectToAction(nameof(Index));

        var userId = User.GetRequiredUserId();
        var page = await _history.GetPageAsync(new HistoryQuery(userId, 1, 200, Tag: tag), ct);
        var viewed = await _views.ViewedAsync(userId, page.Items, ct);
        return View(Views.Filter, new BookmarkFilterViewModel
        {
            Token = tag, Kind = "tag", Items = page.Items.Select(e => e.ToItemView(viewed)).ToList(),
        });
    }

    [HttpGet("/bookmarks")]
    public async Task<IActionResult> Index(string? artist, CancellationToken ct)
    {
        var userId = User.GetRequiredUserId();

        // Artists now have their own page (display image + all their gens + set-display).
        if (!string.IsNullOrEmpty(artist))
            return Redirect("/artist/" + Uri.EscapeDataString(artist));

        var tokens = await _bookmarks.GetTokensAsync(userId, ct);
        var images = await _bookmarks.GetImagesAsync(userId, ct);

        var artists = tokens.Where(t => t.Kind == TokenKind.Artist).ToList();
        var tags = tokens.Where(t => t.Kind == TokenKind.Tag).ToList();
        var displays = await _artists.ResolveManyAsync(userId, artists.Select(a => a.Name).ToList(), ct);
        var tagDisplays = await _tags.ResolveManyAsync(userId, tags.Select(t => t.Name).ToList(), ct);
        ArtistCard Card(Domain.Entities.TokenBookmark t) =>
            new() { Name = t.Name, DisplayImageId = displays.GetValueOrDefault(t.Name), Pinned = t.PinnedAtUtc is not null };
        TagCard TagCardOf(Domain.Entities.TokenBookmark t) =>
            new(t.Name, TagCategory.Slug(_tagCatalog.Lookup(t.Name)?.Type ?? 0), tagDisplays.GetValueOrDefault(t.Name));

        // Global = bookmarks with no category; then one group per category. A multi-category bookmark appears once per
        // category (never in Global). Within a group, artists list pinned-first (stable sort keeps the saved order
        // among the rest, and among pins orders by most-recently-pinned).
        static bool InGlobal(IReadOnlyList<string> cats) => cats.Count == 0;
        static bool InCategory(IReadOnlyList<string> cats, string name) =>
            cats.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

        BookmarkGroup Build(string title, bool isGlobal, Func<IReadOnlyList<string>, bool> pred)
        {
            var groupArtists = artists.Where(a => pred(a.Categories))
                .OrderByDescending(t => t.PinnedAtUtc is not null)
                .ThenByDescending(t => t.PinnedAtUtc)
                .Select(Card)
                .ToList();
            return new BookmarkGroup
            {
                Title = title,
                IsGlobal = isGlobal,
                Artists = groupArtists,
                Tags = tags.Where(t => pred(t.Categories)).Select(TagCardOf).ToList(),
                Images = images.Where(i => pred(i.Categories)).Select(b => b.ToBookmarkView()).ToList(),
            };
        }

        static bool HasContent(BookmarkGroup g) => g.Artists.Count > 0 || g.Tags.Count > 0 || g.Images.Count > 0;

        var categoryNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in tokens.SelectMany(t => t.Categories).Concat(images.SelectMany(i => i.Categories)))
            categoryNames.Add(c);

        var groups = new List<BookmarkGroup>();
        var global = Build(GroupTitles.Global, true, InGlobal);
        if (HasContent(global))
            groups.Add(global);
        foreach (var name in categoryNames)
        {
            var g = Build(name, false, cats => InCategory(cats, name));
            if (HasContent(g))
                groups.Add(g);
        }

        return View(new BookmarksViewModel { Groups = groups });
    }

    /// <summary>View names this controller renders.</summary>
    private static class Views
    {
        /// <summary>The filtered images view for a single starred tag.</summary>
        public const string Filter = "Filter";
    }

    /// <summary>Display titles for the built-in bookmark groups.</summary>
    private static class GroupTitles
    {
        /// <summary>The group holding bookmarks that belong to no category.</summary>
        public const string Global = "Global";
    }
}
