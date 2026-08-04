using ImageGen.Application.Services;
using ImageGen.Domain;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;

namespace ImageGen.Api.Endpoints;

public static class BanEndpoints
{
    public static void MapBanEndpoints(this RouteGroupBuilder api)
    {
        // Every model's bans for this user, grouped — used by the Settings manager. There is no per-model GET: nothing
        // client-side needs one. The detail view renders its banned chips from BanService directly, and the generate
        // path resolves bans in the worker rather than having the browser hand them over.
        api.MapGet("/bans/all", async (HttpContext context, BanService bans) =>
        {
            var userId = context.User.GetRequiredUserId();
            var list = await bans.GetAllAsync(userId, context.RequestAborted);
            var groups = list
                .GroupBy(b => b.ModelId, StringComparer.Ordinal)
                .Select(g => new ModelBansGroup
                {
                    ModelId = g.Key,
                    Artists = g.Where(b => b.Kind == TokenKind.Artist).Select(b => b.Name).ToList(),
                    Tags = g.Where(b => b.Kind == TokenKind.Tag).Select(b => b.Name).ToList(),
                })
                .ToList();
            return Results.Ok(groups);
        });

        api.MapPost("/bans", async (HttpContext context, BanService bans) =>
        {
            var request = await Json.ReadAsync<BanRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.ModelId) || string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest();

            var userId = context.User.GetRequiredUserId();
            var added = await bans.AddAsync(
                userId, request.ModelId, request.Name, WireMapping.ParseKind(request.Kind), context.RequestAborted);
            return Results.Ok(new { added });
        });

        api.MapDelete("/bans", async (HttpContext context, BanService bans, string modelId, string name, string kind) =>
        {
            var userId = context.User.GetRequiredUserId();
            var removed = await bans.RemoveAsync(
                userId, modelId, name, WireMapping.ParseKind(kind), context.RequestAborted);
            return removed ? Results.NoContent() : Results.NotFound();
        });
    }
}
