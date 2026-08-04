//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Services;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;

namespace ImageGen.Api.Endpoints;

public static class TagEndpoints
{
    public static void MapTagEndpoints(this RouteGroupBuilder api)
    {
        // Pick the image that represents a tag for this user (must be one of their own generations).
        api.MapPost("/tag/display", async (HttpContext context, TagService tags, TimeProvider clock) =>
        {
            var req = await Json.ReadAsync<TagDisplayRequest>(context);
            if (req is null || string.IsNullOrWhiteSpace(req.Tag) || string.IsNullOrWhiteSpace(req.Id))
                return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            var ok = await tags.SetAsync(userId, req.Tag, req.Id, clock.GetUtcNow().UtcDateTime, context.RequestAborted);
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        // Clear the portrait so the tag shows a placeholder again.
        api.MapDelete("/tag/display", async (HttpContext context, TagService tags, string tag) =>
        {
            if (string.IsNullOrWhiteSpace(tag))
                return Results.BadRequest();
            var userId = context.User.GetUserId()!.Value;
            await tags.ClearAsync(userId, tag, context.RequestAborted);
            return Results.NoContent();
        });
    }
}
