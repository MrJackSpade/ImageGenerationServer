using ImageGen.Application.Services;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Api.Endpoints;

public static class LoraEndpoints
{
    public static void MapLoraEndpoints(this RouteGroupBuilder api)
    {
        // Save a LoRA's trigger-word override + auto-attach preference (the LoRA manager page).
        api.MapPost(Routes.LoraSettings, async (HttpContext context, ILoraUserSettingRepository settings) =>
        {
            var req = await Json.ReadAsync<LoraSettingsRequest>(context);
            if (req is null || string.IsNullOrWhiteSpace(req.Lora))
                return Results.BadRequest();
            var userId = context.User.GetRequiredUserId();
            await settings.SetAsync(new LoraUserSetting
            {
                UserId = userId,
                LoraName = req.Lora,
                TriggerWords = string.IsNullOrWhiteSpace(req.Triggers) ? null : req.Triggers.Trim(),
                AutoAttach = req.AutoAttach,
            }, context.RequestAborted);
            return Results.Ok(new { ok = true });
        });

        // Pick the image that represents a LoRA for this user (must be one of their own generations).
        api.MapPost(Routes.LoraDisplay, async (HttpContext context, LoraService loras, TimeProvider clock) =>
        {
            var req = await Json.ReadAsync<LoraDisplayRequest>(context);
            if (req is null || string.IsNullOrWhiteSpace(req.Lora) || string.IsNullOrWhiteSpace(req.Id))
                return Results.BadRequest();

            var userId = context.User.GetRequiredUserId();
            var ok = await loras.SetAsync(userId, req.Lora, req.Id, clock.GetUtcNow().UtcDateTime, context.RequestAborted);
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        // Clear the cover so the LoRA shows a placeholder again in the picker.
        api.MapDelete(Routes.LoraDisplay, async (HttpContext context, LoraService loras, string lora) =>
        {
            if (string.IsNullOrWhiteSpace(lora))
                return Results.BadRequest();
            var userId = context.User.GetRequiredUserId();
            await loras.ClearAsync(userId, lora, context.RequestAborted);
            return Results.NoContent();
        });
    }

    /// <summary>Route templates for the LoRA endpoints.</summary>
    private static class Routes
    {
        /// <summary>A LoRA's trigger-word override + auto-attach preference.</summary>
        public const string LoraSettings = "/lora/settings";

        /// <summary>The image that represents a LoRA for this user.</summary>
        public const string LoraDisplay = "/lora/display";
    }
}
