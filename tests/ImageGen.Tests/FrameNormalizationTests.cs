using ImageGen.Comfy;

namespace ImageGen.Tests;

/// <summary>
/// Video length is authored in FRAMES but offered to the composer in SECONDS (issue #194). <see cref="FrameNormalization"/>
/// is the one place that turns the seconds a user types into a frame count valid for the model — converting with the
/// model's own fps, then snapping to its stepped cadence — and also snaps a length given directly in frames. These
/// lock both directions and the "each seam is idempotent" property the twin Normalize passes rely on.
/// </summary>
public sealed class FrameNormalizationTests
{
    private static Dictionary<string, object?> Bag(params (string Key, object? Value)[] pairs)
    {
        Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string k, object? val) in pairs)
        {
            v[k] = val;
        }

        return v;
    }

    [Theory]
    // seconds, fps, rule(base,step) -> frames.  Round to the nearest frame, then snap UP to Base + k*Step.
    [InlineData(4.0, 24.0, 1, 8, 97)]   // LTX 8n+1 @ 24fps: round(96)=96 -> 97
    [InlineData(5.0, 16.0, 1, 4, 81)]   // Wan  4n+1 @ 16fps: round(80)=80 -> 81
    [InlineData(2.0, 24.0, 5, 17, 56)]  // MiniMax-H3 17n+5 @ 24fps: round(48)=48 -> 56 (5+3*17)
    [InlineData(3.0, 16.0, 1, 4, 49)]   // Wan @16fps: round(48)=48 -> 49
    public void Seconds_convert_to_a_cadence_valid_frame_count(double seconds, double fps, int @base, int step, int expected)
    {
        Dictionary<string, object?> p = Bag(
            (WorkflowParamKeys.DurationSeconds, seconds),
            (WorkflowParamKeys.Fps, fps));

        _ = FrameNormalization.Apply(new FrameRule(@base, step), p);

        Assert.Equal(expected, p[WorkflowParamKeys.Length]);
    }

    [Fact]
    public void A_seconds_value_that_lands_off_the_grid_produces_a_notice()
    {
        // 4.7s @ 24fps on Wan's 4n+1 grid = round(112.8)=113 -> snapped to 113 (113 = 4*28+1, already valid) — so
        // pick a value that genuinely snaps: 4.6s -> round(110.4)=110 -> 113. The user gets told their length moved.
        Dictionary<string, object?> p = Bag(
            (WorkflowParamKeys.DurationSeconds, 4.6),
            (WorkflowParamKeys.Fps, 24.0));

        IReadOnlyList<string> notices = FrameNormalization.Apply(new FrameRule(1, 4), p);

        Assert.Equal(113, p[WorkflowParamKeys.Length]);
        Assert.Equal(1, notices.Count);
    }

    [Fact]
    public void A_seconds_value_already_on_the_grid_is_silent()
    {
        // A length whose seconds land exactly on a valid frame count (97 = 8*12+1 at 24fps) needs no adjustment, so
        // the user isn't told anything moved.
        Dictionary<string, object?> p = Bag(
            (WorkflowParamKeys.DurationSeconds, 97.0 / 24.0),
            (WorkflowParamKeys.Fps, 24.0));

        IReadOnlyList<string> notices = FrameNormalization.Apply(new FrameRule(1, 8), p);

        Assert.Equal(97, p[WorkflowParamKeys.Length]);
        Assert.Empty(notices);
    }

    [Fact]
    public void Without_fps_the_seconds_input_is_left_for_the_frame_snap()
    {
        // No fps in the bag → nothing to convert with. The seconds key is ignored and the existing frame `length`
        // (if any) still snaps, rather than a made-up fps being substituted.
        Dictionary<string, object?> p = Bag(
            (WorkflowParamKeys.DurationSeconds, 5.0),
            (WorkflowParamKeys.Length, 30));

        _ = FrameNormalization.Apply(new FrameRule(1, 8), p);

        Assert.Equal(33, p[WorkflowParamKeys.Length]);   // 30 -> 33 (8n+1), seconds untouched
    }

    [Theory]
    [InlineData(30, 1, 8, 33)]    // LTX: 30 -> 33
    [InlineData(30, 1, 4, 33)]    // Wan: 30 -> 33
    [InlineData(97, 1, 8, 97)]    // already valid -> unchanged
    public void A_direct_frame_count_snaps_up_to_the_cadence(int length, int @base, int step, int expected)
    {
        Dictionary<string, object?> p = Bag((WorkflowParamKeys.Length, length));

        _ = FrameNormalization.Apply(new FrameRule(@base, step), p);

        Assert.Equal(expected, p[WorkflowParamKeys.Length]);
    }

    [Fact]
    public void No_frame_rule_means_no_change()
    {
        Dictionary<string, object?> p = Bag(
            (WorkflowParamKeys.DurationSeconds, 5.0),
            (WorkflowParamKeys.Fps, 24.0),
            (WorkflowParamKeys.Length, 30));

        IReadOnlyList<string> notices = FrameNormalization.Apply(null, p);

        Assert.Empty(notices);
        Assert.Equal(30, p[WorkflowParamKeys.Length]);   // untouched — a still / unconstrained model
    }

    [Fact]
    public void The_conversion_is_idempotent_across_the_two_normalize_passes()
    {
        // Normalize runs at enqueue AND submit on the same bag shape. Applying twice must not drift the frame count.
        Dictionary<string, object?> p = Bag(
            (WorkflowParamKeys.DurationSeconds, 4.6),
            (WorkflowParamKeys.Fps, 24.0));
        FrameRule rule = new(1, 4);

        _ = FrameNormalization.Apply(rule, p);
        object? afterFirst = p[WorkflowParamKeys.Length];
        _ = FrameNormalization.Apply(rule, p);

        Assert.Equal(afterFirst, p[WorkflowParamKeys.Length]);
    }
}
