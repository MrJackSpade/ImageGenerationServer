using System.Globalization;

namespace ImageGen.Comfy;

/// <summary>
/// The pre-build frame-count normalization shared by both <see cref="IWorkflow.Normalize"/> defaults (the interface
/// default and the <see cref="Workflow{TParams}"/> override, which must stay in lock-step). Two steps, in order:
/// <list type="number">
/// <item>Video length entered in SECONDS → frames. The composer offers a stepped video model's length as seconds
/// (issue #194); the graph reads <c>length</c> in frames. Converts with the model's own <c>fps</c> then snaps to the
/// cadence, so the seconds a user types always land on a valid clip length.</item>
/// <item>A length given directly in FRAMES → snapped to the cadence, the long-standing behaviour.</item>
/// </list>
/// Both run on the loose bag before it is deserialized into a typed params DTO, and both are idempotent so the
/// submit pass is a no-op once the enqueue pass has already normalized.
/// </summary>
public static class FrameNormalization
{
    /// <summary>Apply the seconds→frames conversion and cadence snap to <paramref name="p"/> in place, returning one
    /// human-readable notice per user-visible adjustment (none when nothing changed, or when the workflow declares no
    /// <see cref="FrameRule"/>).</summary>
    public static IReadOnlyList<string> Apply(FrameRule? rule, IDictionary<string, object?> p)
    {
        List<string> notices = [];
        if (rule is not { } fr)
        {
            return notices;
        }

        // Seconds → frames. `fps` rides in the same bag (a per-config scalar); without it there is nothing to convert
        // with, so the seconds input is left for the frame snap below to handle as-is.
        if (p.TryGetValue(WorkflowParamKeys.DurationSeconds, out object? rawSeconds) && rawSeconds is not null)
        {
            double seconds = ParamsCodec.AsDouble(rawSeconds);
            double fps = p.TryGetValue(WorkflowParamKeys.Fps, out object? rawFps) && rawFps is not null
                ? ParamsCodec.AsDouble(rawFps)
                : 0;
            if (seconds > 0 && fps > 0)
            {
                int rawFrames = (int)Math.Round(seconds * fps);
                // NEAREST, not up: the composer offers seconds in tenths but the model's lengths come in coarser
                // steps, so a chosen second falls between two valid lengths. Rounding to the closest is the honest
                // answer — and the notice states the length actually rendered so the value isn't silently swapped.
                int frames = fr.SnapNearest(rawFrames);
                p[WorkflowParamKeys.Length] = frames;
                if (frames != rawFrames)
                {
                    notices.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{seconds:0.##}s isn’t an exact length for this model — rendering {frames} frames (~{frames / fps:0.##}s)."));
                }
            }
        }

        // A length given directly in frames — snapped to the model's stepped cadence.
        if (p.TryGetValue(WorkflowParamKeys.Length, out object? raw) && raw is not null)
        {
            int req = ParamsCodec.AsInt(raw);
            if (req > 0)
            {
                int snapped = fr.Snap(req);
                if (snapped != req)
                {
                    p[WorkflowParamKeys.Length] = snapped;
                    notices.Add($"{req} frames isn’t valid for this model — rendering {snapped} (frame count must be {fr.Step}n+{fr.Base}).");
                }
            }
        }

        return notices;
    }
}
