//TODO: CHECK FOR FALLBACKS
using ImageGen.Api.Auth;
using ImageGen.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ImageGen.Api;

/// <summary>Mounts the JSON API: the client-action endpoints under <c>/api</c> and the render (ex-Forge) endpoints
/// under <c>/forge</c>. Kept out of the controllers so the API surface is isolated.</summary>
public static class ApiEndpointExtensions
{
    /// <summary>Map the <c>/api</c> and <c>/forge</c> endpoint groups. Requires cookie/API-key auth to be configured
    /// and (for <c>/forge/ws</c>) WebSockets enabled in the host pipeline.</summary>
    public static void MapImageGenApi(this IEndpointRouteBuilder app)
    {
        // Client-side action endpoints.
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapHistoryEndpoints();
        api.MapBookmarkEndpoints();
        api.MapBanEndpoints();
        api.MapPendingEndpoints();
        api.MapArtistEndpoints();
        api.MapLoraEndpoints();
        api.MapTagEndpoints();
        api.MapSettingsEndpoints();

        // The render backend under /forge. Gated: the caller must be authenticated (a login cookie or a per-user
        // X-Api-Key). Every job is owned by that real user; the resolved id + caller scope are stashed for the queue.
        var forge = app.MapGroup("/forge");
        forge.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            // Endpoints opting out (e.g. /healthz for liveness probes) skip the auth gate entirely.
            if (http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
                return await next(ctx);
            if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            var owner = http.User.GetUserId();
            if (owner is null) return Results.Unauthorized();
            http.Items["ForgeOwnerUserId"] = owner.Value;
            // API-key callers (flagged by the API-key middleware) get the api-visible list; browsers the ui-visible list.
            http.Items["ForgeScope"] = http.Items.ContainsKey("AuthViaApiKey") ? "api" : "ui";
            return await next(ctx);
        });
        forge.MapForgeApi();
    }
}
