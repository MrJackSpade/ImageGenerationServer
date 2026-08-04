//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Services;
using ImageGen.Domain;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;

namespace ImageGen.Api.Endpoints;

public static class BookmarkEndpoints
{
    public static void MapBookmarkEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/bookmarks", async (HttpContext context, BookmarkService bookmarks) =>
        {
            var userId = context.User.GetUserId()!.Value;
            var tokens = await bookmarks.GetTokensAsync(userId, context.RequestAborted);
            var images = await bookmarks.GetImagesAsync(userId, context.RequestAborted);
            return Results.Ok(new BookmarksResponse
            {
                Artists = tokens.Where(t => t.Kind == TokenKind.Artist).Select(t => t.Name).ToList(),
                Tags = tokens.Where(t => t.Kind == TokenKind.Tag).Select(t => t.Name).ToList(),
                Images = images.Select(i => i.ToContract()).ToList(),
            });
        });

        api.MapPost("/bookmarks/tokens", async (HttpContext context, BookmarkService bookmarks) =>
        {
            var request = await Json.ReadAsync<TokenBookmarkRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            var added = await bookmarks.AddTokenAsync(userId, request.Name, WireMapping.ParseKind(request.Kind), context.RequestAborted);
            return Results.Ok(new { added });
        });

        api.MapDelete("/bookmarks/tokens", async (HttpContext context, BookmarkService bookmarks, string name, string kind) =>
        {
            var userId = context.User.GetUserId()!.Value;
            var removed = await bookmarks.RemoveTokenAsync(userId, name, WireMapping.ParseKind(kind), context.RequestAborted);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        api.MapPost("/bookmarks/tokens/pin", async (HttpContext context, BookmarkService bookmarks) =>
        {
            var request = await Json.ReadAsync<PinTokenRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            var updated = await bookmarks.SetTokenPinnedAsync(
                userId, request.Name, WireMapping.ParseKind(request.Kind), request.Pinned, context.RequestAborted);
            return updated ? Results.NoContent() : Results.NotFound();
        });

        api.MapPost("/bookmarks/images", async (HttpContext context, BookmarkService bookmarks) =>
        {
            var record = await Json.ReadAsync<ImageBookmarkContract>(context);
            if (record is null || string.IsNullOrEmpty(record.Id))
                return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            var added = await bookmarks.AddImageAsync(record.ToAddImageCommand(userId), context.RequestAborted);
            return Results.Ok(new { added });
        });

        api.MapDelete("/bookmarks/images", async (HttpContext context, BookmarkService bookmarks, string id) =>
        {
            var userId = context.User.GetUserId()!.Value;
            var removed = await bookmarks.RemoveImageAsync(userId, id, context.RequestAborted);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // Long-press dialog: all categories the user has, plus the ones the queried item is filed under.
        // scope=token needs name+kind; scope=image needs id. Missing/blank selectors just return an empty Selected.
        api.MapGet("/bookmarks/categories", async (
            HttpContext context, BookmarkService bookmarks, string? scope, string? name, string? kind, string? id) =>
        {
            var userId = context.User.GetUserId()!.Value;
            var all = await bookmarks.GetAllCategoriesAsync(userId, context.RequestAborted);
            IReadOnlyList<string> selected = [];
            if (string.Equals(scope, "token", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(name))
                selected = await bookmarks.GetTokenCategoriesAsync(
                    userId, name, WireMapping.ParseKind(kind ?? ""), context.RequestAborted);
            else if (string.Equals(scope, "image", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(id))
                selected = await bookmarks.GetImageCategoriesAsync(userId, id, context.RequestAborted);
            return Results.Ok(new CategoriesResponse { All = all, Selected = selected });
        });

        api.MapPost("/bookmarks/tokens/categories", async (HttpContext context, BookmarkService bookmarks) =>
        {
            var request = await Json.ReadAsync<SetTokenCategoriesRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            await bookmarks.SetTokenCategoriesAsync(
                userId, request.Name, WireMapping.ParseKind(request.Kind), request.Categories ?? [], context.RequestAborted);
            return Results.NoContent();
        });

        api.MapPost("/bookmarks/images/categories", async (HttpContext context, BookmarkService bookmarks) =>
        {
            var request = await Json.ReadAsync<SetImageCategoriesRequest>(context);
            if (request is null || request.Image is null || string.IsNullOrEmpty(request.Image.Id))
                return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            await bookmarks.SetImageCategoriesAsync(
                request.Image.ToAddImageCommand(userId), request.Categories ?? [], context.RequestAborted);
            return Results.NoContent();
        });
    }
}
