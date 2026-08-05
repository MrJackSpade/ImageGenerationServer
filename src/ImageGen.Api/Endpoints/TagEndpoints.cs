using ImageGen.Application.Services;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;

namespace ImageGen.Api.Endpoints;

public static class TagEndpoints
{
    public static void MapTagEndpoints(this RouteGroupBuilder api)
    {
        // Pick the image that represents a tag for this user (must be one of their own generations).
        api.MapPost(Routes.TagDisplay, async (HttpContext context, TagService tags, TimeProvider clock) =>
        {
            var req = await Json.ReadAsync<TagDisplayRequest>(context);
            if (req is null || string.IsNullOrWhiteSpace(req.Tag) || string.IsNullOrWhiteSpace(req.Id))
                return Results.BadRequest();

            var userId = context.User.GetRequiredUserId();
            var ok = await tags.SetAsync(userId, req.Tag, req.Id, clock.GetUtcNow().UtcDateTime, context.RequestAborted);
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        // Clear the manual pick so the tag falls back to the user's most recent generation carrying it (else a placeholder).
        api.MapDelete(Routes.TagDisplay, async (HttpContext context, TagService tags, string tag) =>
        {
            if (string.IsNullOrWhiteSpace(tag))
                return Results.BadRequest();
            var userId = context.User.GetRequiredUserId();
            await tags.ClearAsync(userId, tag, context.RequestAborted);
            return Results.NoContent();
        });
    }

    /// <summary>Route templates for the tag endpoints.</summary>
    private static class Routes
    {
        /// <summary>The image that represents a tag for this user.</summary>
        public const string TagDisplay = "/tag/display";
    }
}
