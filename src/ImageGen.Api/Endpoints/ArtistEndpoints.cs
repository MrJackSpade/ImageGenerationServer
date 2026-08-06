using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;
using ImageGen.Application.Services;

namespace ImageGen.Api.Endpoints;

public static class ArtistEndpoints
{
    public static void MapArtistEndpoints(this RouteGroupBuilder api)
    {
        // Pick the image that represents an artist for this user (must be one of their own generations).
        _ = api.MapPost(Routes.ArtistDisplay, async (HttpContext context, ArtistService artists, TimeProvider clock) =>
        {
            ArtistDisplayRequest? req = await Json.ReadAsync<ArtistDisplayRequest>(context);
            if (req is null || string.IsNullOrWhiteSpace(req.Artist) || string.IsNullOrWhiteSpace(req.Id))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            bool ok = await artists.SetAsync(userId, req.Artist, req.Id, clock.GetUtcNow().UtcDateTime, context.RequestAborted);
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        // Clear the pick so the artist falls back to the user's most recent generation for it.
        _ = api.MapDelete(Routes.ArtistDisplay, async (HttpContext context, ArtistService artists, string artist) =>
        {
            if (string.IsNullOrWhiteSpace(artist))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            await artists.ClearAsync(userId, artist, context.RequestAborted);
            return Results.NoContent();
        });
    }

    /// <summary>Route templates for the artist endpoints.</summary>
    private static class Routes
    {
        /// <summary>The image that represents an artist for this user.</summary>
        public const string ArtistDisplay = "/artist/display";
    }
}
