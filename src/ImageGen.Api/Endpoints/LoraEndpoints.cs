using ImageGen.Application.Services;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;

namespace ImageGen.Api.Endpoints;

public static class LoraEndpoints
{
    public static void MapLoraEndpoints(this RouteGroupBuilder api)
    {
        // Pick the image that represents a LoRA for this user (must be one of their own generations).
        api.MapPost("/lora/display", async (HttpContext context, LoraService loras, TimeProvider clock) =>
        {
            var req = await Json.ReadAsync<LoraDisplayRequest>(context);
            if (req is null || string.IsNullOrWhiteSpace(req.Lora) || string.IsNullOrWhiteSpace(req.Id))
                return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            var ok = await loras.SetAsync(userId, req.Lora, req.Id, clock.GetUtcNow().UtcDateTime, context.RequestAborted);
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        // Clear the cover so the LoRA shows a placeholder again in the picker.
        api.MapDelete("/lora/display", async (HttpContext context, LoraService loras, string lora) =>
        {
            if (string.IsNullOrWhiteSpace(lora))
                return Results.BadRequest();
            var userId = context.User.GetUserId()!.Value;
            await loras.ClearAsync(userId, lora, context.RequestAborted);
            return Results.NoContent();
        });
    }
}
