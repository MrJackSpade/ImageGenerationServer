using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;
using ImageGen.Application.Services;
using ImageGen.Domain;
using ImageGen.Domain.Entities;

namespace ImageGen.Api.Endpoints;

public static class BookmarkEndpoints
{
    public static void MapBookmarkEndpoints(this RouteGroupBuilder api)
    {
        _ = api.MapGet(Routes.Bookmarks, async (HttpContext context, BookmarkService bookmarks) =>
        {
            long userId = context.User.GetRequiredUserId();
            IReadOnlyList<TokenBookmark> tokens = await bookmarks.GetTokensAsync(userId, context.RequestAborted);
            IReadOnlyList<ImageBookmark> images = await bookmarks.GetImagesAsync(userId, context.RequestAborted);
            return Results.Ok(new BookmarksResponse
            {
                Artists = [.. tokens.Where(t => t.Kind == TokenKind.Artist).Select(t => t.Name)],
                Tags = [.. tokens.Where(t => t.Kind == TokenKind.Tag).Select(t => t.Name)],
                Images = [.. images.Select(i => i.ToContract())],
            });
        });

        _ = api.MapPost(Routes.Tokens, async (HttpContext context, BookmarkService bookmarks) =>
        {
            TokenBookmarkRequest? request = await Json.ReadAsync<TokenBookmarkRequest>(context);
            if (request is null
                || string.IsNullOrWhiteSpace(request.Name)
                || !WireMapping.TryParseKind(request.Kind, out TokenKind kind))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            bool added = await bookmarks.AddTokenAsync(userId, request.Name, kind, context.RequestAborted);
            return Results.Ok(new { added });
        });

        _ = api.MapDelete(Routes.Tokens, async (HttpContext context, BookmarkService bookmarks, string name, string kind) =>
        {
            if (!WireMapping.TryParseKind(kind, out TokenKind parsed))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            bool removed = await bookmarks.RemoveTokenAsync(userId, name, parsed, context.RequestAborted);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        _ = api.MapPost(Routes.TokensPin, async (HttpContext context, BookmarkService bookmarks) =>
        {
            PinTokenRequest? request = await Json.ReadAsync<PinTokenRequest>(context);
            if (request is null
                || string.IsNullOrWhiteSpace(request.Name)
                || !WireMapping.TryParseKind(request.Kind, out TokenKind kind))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            bool updated = await bookmarks.SetTokenPinnedAsync(
                userId, request.Name, kind, request.Pinned, context.RequestAborted);
            return updated ? Results.NoContent() : Results.NotFound();
        });

        _ = api.MapPost(Routes.Images, async (HttpContext context, BookmarkService bookmarks) =>
        {
            ImageBookmarkContract? record = await Json.ReadAsync<ImageBookmarkContract>(context);
            if (record is null || string.IsNullOrEmpty(record.Id))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            bool added = await bookmarks.AddImageAsync(record.ToAddImageCommand(userId), context.RequestAborted);
            return Results.Ok(new { added });
        });

        _ = api.MapDelete(Routes.Images, async (HttpContext context, BookmarkService bookmarks, string id) =>
        {
            long userId = context.User.GetRequiredUserId();
            bool removed = await bookmarks.RemoveImageAsync(userId, id, context.RequestAborted);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // Long-press dialog: all categories the user has, plus the ones the queried item is filed under.
        // scope=token needs name+kind; scope=image needs id. Missing/blank selectors just return an empty Selected.
        _ = api.MapGet(Routes.Categories, async (
            HttpContext context, BookmarkService bookmarks, string? scope, string? name, string? kind, string? id) =>
        {
            string tokenName = name ?? string.Empty;
            bool tokenScope = string.Equals(scope, Scopes.Token, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(tokenName);
            TokenKind tokenKind = default;
            if (tokenScope && !WireMapping.TryParseKind(kind, out tokenKind))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            IReadOnlyList<string> all = await bookmarks.GetAllCategoriesAsync(userId, context.RequestAborted);
            IReadOnlyList<string> selected = [];
            if (tokenScope)
            {
                selected = await bookmarks.GetTokenCategoriesAsync(
                    userId, tokenName, tokenKind, context.RequestAborted);
            }
            else if (string.Equals(scope, Scopes.Image, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(id))
            {
                selected = await bookmarks.GetImageCategoriesAsync(userId, id, context.RequestAborted);
            }

            return Results.Ok(new CategoriesResponse { All = all, Selected = selected });
        });

        _ = api.MapPost(Routes.TokenCategories, async (HttpContext context, BookmarkService bookmarks) =>
        {
            SetTokenCategoriesRequest? request = await Json.ReadAsync<SetTokenCategoriesRequest>(context);
            if (request is null
                || string.IsNullOrWhiteSpace(request.Name)
                || !WireMapping.TryParseKind(request.Kind, out TokenKind kind))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await bookmarks.SetTokenCategoriesAsync(
                userId, request.Name, kind, request.Categories, context.RequestAborted);
            return Results.NoContent();
        });

        _ = api.MapPost(Routes.ImageCategories, async (HttpContext context, BookmarkService bookmarks) =>
        {
            SetImageCategoriesRequest? request = await Json.ReadAsync<SetImageCategoriesRequest>(context);
            if (request is null || request.Image is null || string.IsNullOrEmpty(request.Image.Id))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await bookmarks.SetImageCategoriesAsync(
                request.Image.ToAddImageCommand(userId), request.Categories, context.RequestAborted);
            return Results.NoContent();
        });
    }

    /// <summary>Route templates for the bookmark endpoints.</summary>
    private static class Routes
    {
        /// <summary>The user's bookmarked tokens and images.</summary>
        public const string Bookmarks = "/bookmarks";

        /// <summary>A bookmarked token (artist/tag).</summary>
        public const string Tokens = "/bookmarks/tokens";

        /// <summary>A bookmarked token's pinned flag.</summary>
        public const string TokensPin = "/bookmarks/tokens/pin";

        /// <summary>A bookmarked image.</summary>
        public const string Images = "/bookmarks/images";

        /// <summary>All categories, plus the ones a queried token or image is filed under.</summary>
        public const string Categories = "/bookmarks/categories";

        /// <summary>The categories a token is filed under.</summary>
        public const string TokenCategories = "/bookmarks/tokens/categories";

        /// <summary>The categories an image is filed under.</summary>
        public const string ImageCategories = "/bookmarks/images/categories";
    }

    /// <summary>The <c>scope</c> query values the categories lookup accepts.</summary>
    private static class Scopes
    {
        /// <summary>Look up the categories of a token (needs name + kind).</summary>
        public const string Token = "token";

        /// <summary>Look up the categories of an image (needs id).</summary>
        public const string Image = "image";
    }
}
