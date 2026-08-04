using ImageGen.Application.Services;
using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;

namespace ImageGen.Api.Endpoints;

public static class PendingEndpoints
{
    public static void MapPendingEndpoints(this RouteGroupBuilder api)
    {
        // This user's in-flight jobs, so any of their devices can show what's currently rendering (with live
        // progress polled from the gateway). The originating tab still tracks its own job directly.
        api.MapGet("/pending", async (HttpContext context, PendingJobService pending) =>
        {
            var userId = context.User.GetUserId()!.Value;
            var jobs = await pending.ListForUserAsync(userId, context.RequestAborted);
            return Results.Ok(jobs.Select(j => j.ToView()).ToList());
        });

        // The client registers each gateway job it submits; the reconciler (PendingJobReconciler) takes it
        // from here, so the result is persisted even if this browser closes before seeing it.
        api.MapPost("/pending", async (HttpContext context, PendingJobService pending, TimeProvider clock) =>
        {
            var record = await Json.ReadAsync<PendingJobContract>(context);
            if (record is null || string.IsNullOrWhiteSpace(record.JobId))
                return Results.BadRequest();

            var userId = context.User.GetUserId()!.Value;
            var command = record.ToRegisterPendingJobCommand(userId, clock.GetUtcNow().UtcDateTime);
            await pending.RegisterAsync(command, context.RequestAborted);
            return Results.Ok(new { ok = true });
        });
    }
}
