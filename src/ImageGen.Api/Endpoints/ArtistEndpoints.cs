//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Services;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;

namespace ImageGen.Api.Endpoints;

public static class ArtistEndpoints
{
    public static void MapArtistEndpoints(this RouteGroupBuilder api)
    {
        // Pick the image that represents an artist for this user (must be one of their own generations).
        api.MapPost("/artist/display", async (HttpContext context, ArtistService artists, TimeProvider clock) =>
        {
            var req = await Json.ReadAsync<ArtistDisplayRequest>(context);
            if (req is null || string.IsNullOrWhiteSpace(req.Artist) || string.IsNullOrWhiteSpace(req.Id))
                return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            var ok = await artists.SetAsync(userId, req.Artist, req.Id, clock.GetUtcNow().UtcDateTime, context.RequestAborted);
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        // Clear the pick so the artist falls back to the user's most recent generation for it.
        api.MapDelete("/artist/display", async (HttpContext context, ArtistService artists, string artist) =>
        {
            if (string.IsNullOrWhiteSpace(artist))
                return Results.BadRequest();
            var userId = context.User.GetUserId()!.Value;
            await artists.ClearAsync(userId, artist, context.RequestAborted);
            return Results.NoContent();
        });
    }
}
