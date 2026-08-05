using ImageGen.Api.Auth;
using ImageGen.Api.Contracts;
using ImageGen.Application.Models;
using ImageGen.Application.Services;
using ImageGen.Domain.Entities;

namespace ImageGen.Api.Endpoints;

public static class PendingEndpoints
{
    public static void MapPendingEndpoints(this RouteGroupBuilder api)
    {
        // This user's in-flight jobs, so any of their devices can show what's currently rendering (with live
        // progress polled from the gateway). The originating tab still tracks its own job directly.
        api.MapGet(Routes.Pending, async (HttpContext context, PendingJobService pending) =>
        {
            long userId = context.User.GetRequiredUserId();
            IReadOnlyList<PendingJob> jobs = await pending.ListForUserAsync(userId, context.RequestAborted);
            return Results.Ok(jobs.Select(j => j.ToView()).ToList());
        });

        // The client registers each gateway job it submits; the reconciler (PendingJobReconciler) takes it
        // from here, so the result is persisted even if this browser closes before seeing it.
        api.MapPost(Routes.Pending, async (HttpContext context, PendingJobService pending, TimeProvider clock) =>
        {
            PendingJobContract? record = await Json.ReadAsync<PendingJobContract>(context);
            if (record is null || string.IsNullOrWhiteSpace(record.JobId))
                return Results.BadRequest();

            long userId = context.User.GetRequiredUserId();
            RegisterPendingJobCommand command = record.ToRegisterPendingJobCommand(userId, clock.GetUtcNow().UtcDateTime);
            await pending.RegisterAsync(command, context.RequestAborted);
            return Results.Ok(new { ok = true });
        });
    }

    /// <summary>Route templates for the pending-job endpoints.</summary>
    private static class Routes
    {
        /// <summary>This user's in-flight jobs (GET lists them; POST registers one).</summary>
        public const string Pending = "/pending";
    }
}
