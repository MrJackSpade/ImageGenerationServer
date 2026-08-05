using ImageGen.Application.Services;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using ImageGen.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

[Authorize]
public sealed class GalleryController(HistoryService history, ImageViewService views) : Controller
{
    private const int PageSize = 40;
    private readonly HistoryService _history = history;
    private readonly ImageViewService _views = views;

    /// <summary>
    /// First page only; the client (gallery.js) loads the rest via /api/history as you scroll. <paramref name="q"/> is
    /// the search box (entries whose prompt contains every space-separated term) and <paramref name="workflow"/> the
    /// workflow filter; <paramref name="unviewed"/> narrows to images this user has never opened. They combine. All
    /// three are real query-string parameters rather than client state, so a filtered view survives a reload, a
    /// bookmark and the no-JS form submit.
    /// </summary>
    [HttpGet("/gallery")]
    public async Task<IActionResult> Index(string? q, string? workflow, bool? unviewed, CancellationToken ct)
    {
        long userId = User.GetRequiredUserId();
        bool unviewedOnly = unviewed ?? false;
        PagedResult<HistoryEntry> result = await _history.GetPageAsync(
            new HistoryQuery(userId, 1, PageSize, Model: workflow, Search: q, UnviewedOnly: unviewedOnly), ct);
        // The options are the workflows the user has actually used — unfiltered, so the dropdown doesn't shrink to
        // whatever the current filter left standing.
        IReadOnlyList<HistoryWorkflowUse> workflows = await _history.GetUsedWorkflowsAsync(userId, ct);
        // The grid outlines what this user hasn't opened; one lookup covers the whole page.
        IReadOnlySet<string> viewed = await _views.ViewedAsync(userId, result.Items, ct);
        return View(new GalleryViewModel
        {
            Items = result.Items.Select(e => e.ToItemView(viewed)).ToList(),
            Page = result.Page,
            PageSize = result.PageSize,
            Total = result.Total,
            Search = q ?? string.Empty,
            Workflow = workflow ?? string.Empty,
            Workflows = workflows,
            UnviewedOnly = unviewedOnly,
        });
    }
}
