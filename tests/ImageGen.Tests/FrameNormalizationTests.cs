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
    // seconds, fps, rule(base,step) -> frames.  Round to the nearest frame, then snap to the NEAREST Base + k*Step.
    [InlineData(4.0, 24.0, 1, 8, 97)]   // LTX 8n+1 @ 24fps: round(96)=96 -> nearest is 97
    [InlineData(5.0, 16.0, 1, 4, 81)]   // Wan  4n+1 @ 16fps: round(80)=80 -> nearest is 81
    [InlineData(2.0, 24.0, 5, 17, 56)]  // MiniMax-H3 17n+5 @ 24fps: round(48)=48 -> nearest is 56 (5+3*17)
    [InlineData(3.0, 16.0, 1, 4, 49)]   // Wan @16fps: round(48)=48 -> nearest is 49
    [InlineData(5.1, 16.0, 1, 4, 81)]   // Wan @16fps: round(81.6)=82 -> nearest is 81 (DOWN; snap-up would give 85)
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
        // 4.6s @ 24fps on Wan's 4n+1 grid = round(110.4)=110 -> nearest valid is 109. The value moved, so the user is
        // told the length actually rendered rather than it being swapped silently.
        Dictionary<string, object?> p = Bag(
            (WorkflowParamKeys.DurationSeconds, 4.6),
            (WorkflowParamKeys.Fps, 24.0));

        IReadOnlyList<string> notices = FrameNormalization.Apply(new FrameRule(1, 4), p);

        Assert.Equal(109, p[WorkflowParamKeys.Length]);
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

    [Theory]
    [InlineData(typeof(ImageGen.Comfy.Generation.WanA14bT2V.WanA14bT2VWorkflow))]
    [InlineData(typeof(ImageGen.Comfy.Generation.HunyuanVideoT2V.HunyuanVideoT2VWorkflow))]
    [InlineData(typeof(ImageGen.Comfy.Generation.HunyuanVideo15T2V.HunyuanVideo15T2VWorkflow))]
    public void Cadence_sensitive_t2v_workflows_normalize_to_their_4n_plus_1_grid(Type workflowType)
    {
        IWorkflow workflow = Assert.IsAssignableFrom<IWorkflow>(Activator.CreateInstance(workflowType));
        Assert.Equal(new FrameRule(1, 4), workflow.FrameRule);
        Dictionary<string, object?> p = Bag((WorkflowParamKeys.Length, 30));

        _ = workflow.Normalize(p, NormalizeContext.Empty);

        Assert.Equal(33, p[WorkflowParamKeys.Length]);
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

    [Theory]
    // n, base, step -> nearest valid length. Contrast Snap, which always rounds up.
    [InlineData(82, 1, 4, 81)]    // 82 is nearer 81 than 85 — Snap would give 85
    [InlineData(83, 1, 4, 85)]    // 83 is nearer 85
    [InlineData(96, 1, 8, 97)]    // 96 -> 97 (nearer than 89)
    [InlineData(0, 1, 4, 1)]      // at/below base clamps to base
    public void SnapNearest_rounds_to_the_closest_valid_length(int n, int @base, int step, int expected)
        => Assert.Equal(expected, new FrameRule(@base, step).SnapNearest(n));

    [Fact]
    public void SnapNearest_breaks_ties_upward()
        // 3 is equidistant from 1 and 5 on a 4n+1 grid; the tie goes up so the snap never renders fewer than the midpoint.
        => Assert.Equal(5, new FrameRule(1, 4).SnapNearest(3));

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

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(82)]
    public void Untrained_frame_policy_preserves_positive_off_cadence_counts(int frames)
    {
        Dictionary<string, object?> p = Bag((WorkflowParamKeys.Length, frames));

        IReadOnlyList<string> notices = FrameNormalization.Apply(
            new FrameRule(1, 4), p, allowUntrainedFrameCounts: true);

        Assert.Equal(frames, p[WorkflowParamKeys.Length]);
        Assert.Empty(notices);
    }

    [Fact]
    public void Untrained_frame_policy_converts_seconds_without_cadence_snapping()
    {
        Dictionary<string, object?> p = Bag(
            (WorkflowParamKeys.DurationSeconds, 2.5),
            (WorkflowParamKeys.Fps, 24));

        _ = FrameNormalization.Apply(new FrameRule(1, 8), p, allowUntrainedFrameCounts: true);

        Assert.Equal(60, p[WorkflowParamKeys.Length]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void No_frame_policy_allows_a_nonpositive_count(int frames)
    {
        Dictionary<string, object?> p = Bag((WorkflowParamKeys.Length, frames));

        _ = Assert.Throws<ImageGen.Application.Rendering.RenderValidationException>(() =>
            FrameNormalization.Apply(new FrameRule(1, 4), p, allowUntrainedFrameCounts: true));
    }

    [Fact]
    public void Cadence_free_video_policy_preserves_a_positive_frame_count()
    {
        Dictionary<string, object?> p = Bag((WorkflowParamKeys.Length, 23));

        IReadOnlyList<string> notices = FrameNormalization.Apply(
            null, p, allowUntrainedFrameCounts: true);

        Assert.Equal(23, p[WorkflowParamKeys.Length]);
        Assert.Empty(notices);
    }

    [Fact]
    public void Cadence_free_video_policy_still_rejects_zero_frames()
    {
        Dictionary<string, object?> p = Bag((WorkflowParamKeys.Length, 0));

        _ = Assert.Throws<ImageGen.Application.Rendering.RenderValidationException>(() =>
            FrameNormalization.Apply(null, p, allowUntrainedFrameCounts: true));
    }
}
