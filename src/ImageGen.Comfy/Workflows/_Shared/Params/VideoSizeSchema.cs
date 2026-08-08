namespace ImageGen.Comfy;

/// <summary>
/// The shared image-to-video "resolution (MP)" control (#186). Every real-video i2v editor scales its source frame to
/// a megapixel BUDGET to set the clip's size; this exposes that budget as a first-class, per-config numeric control
/// (like BooguEdit's edit-resolution knob) so a user can raise or lower it. Its default lives in each config (the value
/// the workflow previously hardcoded), read as a <c>required double</c> off the params record.
///
/// <para>Declared here rather than on <see cref="EditWorkflowBase.SharedSchema"/> so it appears ONLY on the video
/// editors — an image editor that already carries its own <c>megapixels</c> control (BooguEdit) would otherwise get it
/// twice. Each i2v workflow appends this one shared spec to its schema.</para>
/// </summary>
internal static class VideoSizeSchema
{
    public static readonly ParamSpec Megapixels = new()
    {
        Key = WorkflowParamKeys.Megapixels,
        Type = ParamType.Double,
        Step = 0.01,
        Label = "Video resolution (MP)",
    };
}
