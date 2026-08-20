using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;
using ImageGen.Application.Services;
using ImageGen.Domain;
using ImageGen.Domain.Entities;

namespace ImageGen.Api.Endpoints;

public static class BanEndpoints
{
    public static void MapBanEndpoints(this RouteGroupBuilder api)
    {
        // Every model's bans for this user, grouped — used by the Settings manager. There is no per-model GET: nothing
        // client-side needs one. The detail view renders its banned chips from BanService directly, and the generate
        // path resolves bans in the worker rather than having the browser hand them over.
        _ = api.MapGet(Routes.BansAll, async (HttpContext context, BanService bans) =>
        {
            long userId = context.User.GetRequiredUserId();
            IReadOnlyList<BannedToken> list = await bans.GetAllAsync(userId, context.RequestAborted);
            List<ModelBansGroup> groups = [.. list
                .GroupBy(b => b.ModelId, StringComparer.Ordinal)
                .Select(g => new ModelBansGroup
                {
                    ModelId = g.Key,
                    Artists = [.. g.Where(b => b.Kind == TokenKind.Artist).Select(b => b.Name)],
                    Tags = [.. g.Where(b => b.Kind == TokenKind.Tag).Select(b => b.Name)],
                })];
            return Results.Ok(groups);
        });

        _ = api.MapPost(Routes.Bans, async (HttpContext context, BanService bans) =>
        {
            BanRequest? request = await Json.ReadAsync<BanRequest>(context);
            if (request is null
                || string.IsNullOrWhiteSpace(request.ModelId)
                || string.IsNullOrWhiteSpace(request.Name)
                || !WireMapping.TryParseKind(request.Kind, out TokenKind kind))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            bool added = await bans.AddAsync(
                userId, request.ModelId, request.Name, kind, context.RequestAborted);
            return Results.Ok(new { added });
        });

        _ = api.MapDelete(Routes.Bans, async (HttpContext context, BanService bans, string modelId, string name, string kind) =>
        {
            if (!WireMapping.TryParseKind(kind, out TokenKind parsed))
            {
                return Results.BadRequest();
            }

            long userId = context.User.GetRequiredUserId();
            bool removed = await bans.RemoveAsync(
                userId, modelId, name, parsed, context.RequestAborted);
            return removed ? Results.NoContent() : Results.NotFound();
        });
    }

    /// <summary>Route templates for the ban endpoints.</summary>
    private static class Routes
    {
        /// <summary>Every model's bans for this user, grouped.</summary>
        public const string BansAll = "/bans/all";

        /// <summary>Add or remove a ban for a model.</summary>
        public const string Bans = "/bans";
    }
}
