using ImageGen.Application.Services;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;
using ImageGen.Domain.Repositories;

namespace ImageGen.Api.Endpoints;

public static class HistoryEndpoints
{
    public static void MapHistoryEndpoints(this RouteGroupBuilder api)
    {
        // POST, not GET, and the GET is gone rather than left beside it. `search` is the history page's search box —
        // prompt content by another name — and `tag` is a tag token; in a query string both land in the browser's
        // history and address bar on the user's own machine, plus request logs, proxies and Referer headers. Removing
        // the GET is what makes the leak impossible rather than merely unused. (This endpoint is called only by this
        // app's own JS; it has never been a public read surface.)
        api.MapPost(Routes.HistoryQuery, async (
            HistoryQueryRequest req, HttpContext context, HistoryService history, ImageViewService views) =>
        {
            var userId = context.User.GetRequiredUserId();
            // An out-of-range page/window is REFUSED, not clamped — a silently clamped page comes back looking exactly
            // like a satisfied one (ask for a 10,000-row window and the 200 you get reads as "that's everything").
            var page = req.Page ?? 1;
            var pageSize = req.PageSize ?? 40;
            if (page < HistoryQuery.MinPage)
                return Results.BadRequest(new { error = $"page must be >= {HistoryQuery.MinPage}, got {page}" });
            if (pageSize is < HistoryQuery.MinPageSize or > HistoryQuery.MaxPageSize)
                return Results.BadRequest(new { error = $"pageSize must be between {HistoryQuery.MinPageSize} and {HistoryQuery.MaxPageSize}, got {pageSize}" });
            // `search` is the history page's search box: space-separated terms, ALL of which must appear in the prompt.
            var query = new HistoryQuery(
                userId, page, pageSize, req.Artist, req.Tag, req.Workflow, req.Search,
                req.UnviewedOnly ?? false);
            var result = await history.GetPageAsync(query, context.RequestAborted);
            // One lookup for the page's ids: the grid outlines what this user hasn't opened, and only the server
            // knows that — it has to be the same answer on every device, and it outlives any browser.
            var viewed = await views.ViewedAsync(userId, result.Items, context.RequestAborted);
            return Results.Ok(new HistoryPageResponse
            {
                Items = result.Items.Select(e => e.ToContract(viewed)).ToList(),
                Total = result.Total,
                Page = result.Page,
                PageSize = result.PageSize,
            });
        });

        // The compose page's Recent strip. It asks only how few images it is willing to show; the SERVER decides the
        // rest — how far the window has to stretch to cover the current-or-last batch — because that is a fact of the
        // job table, not of whichever browser tab happened to watch the batch run. The client renders what comes back.
        api.MapGet(Routes.Recents, async (HttpContext context, HistoryService history, ImageViewService views, int? min) =>
        {
            var userId = context.User.GetRequiredUserId();
            // An out-of-range `min` is REFUSED, not clamped. The response carries no window size (see
            // RecentsResponse), so a silently clamped request would come back looking exactly like a satisfied one:
            // ask for 500 and the 200 you get reads as "that is everything there is". The server still stretches
            // the window past `min` on its own to cover the current batch -- that part is deliberate and uncapped.
            const int MaxRecents = 200;
            if (min is int requested && (requested < 1 || requested > MaxRecents))
                return Results.BadRequest(new { error = $"min must be between 1 and {MaxRecents}, got {requested}" });
            var minimum = min ?? 48;
            var items = await history.GetRecentsAsync(userId, minimum, context.RequestAborted);
            var viewed = await views.ViewedAsync(userId, items, context.RequestAborted);
            return Results.Ok(new RecentsResponse { Items = items.Select(e => e.ToContract(viewed)).ToList() });
        });

        // Clear the whole unread backlog. Without this an outline that means "you haven't opened this" can only be
        // cleared one image at a time, which is not a thing anyone will do to a library.
        api.MapPost(Routes.HistoryViewed, async (HttpContext context, ImageViewService views) =>
        {
            var userId = context.User.GetRequiredUserId();
            var marked = await views.MarkAllViewedAsync(userId, context.RequestAborted);
            return Results.Ok(new { marked });
        });

        // NOTE: there is intentionally NO POST /history. History is written exactly once, server-side, by the
        // JobQueue worker the moment an image is produced (the sole writer). A client-side writer + an
        // insert-if-absent repository would let deleted images resurrect, so the browser may only read and delete.

        // id carried in the query string (gateway ids may contain characters awkward in a path segment).
        api.MapDelete(Routes.History, async (HttpContext context, HistoryService history, string id) =>
        {
            var userId = context.User.GetRequiredUserId();
            var removed = await history.DeleteAsync(userId, id, context.RequestAborted);
            return removed ? Results.NoContent() : Results.NotFound();
        });
    }

    /// <summary>Route templates for the history endpoints.</summary>
    private static class Routes
    {
        /// <summary>The history page's search (POST, so terms never land in a query string).</summary>
        public const string HistoryQuery = "/history/query";

        /// <summary>The compose page's Recent strip.</summary>
        public const string Recents = "/recents";

        /// <summary>Mark the whole unread backlog as viewed.</summary>
        public const string HistoryViewed = "/history/viewed";

        /// <summary>Delete one history entry (id in the query string).</summary>
        public const string History = "/history";
    }
}
