namespace ImageGen.Application.Rendering;

/// <summary>
/// Where the backend's live step-progress frames land: the adapter that watches the renderer's progress socket reports
/// each frame's fraction here, keyed by the backend prompt id, and the orchestrator pins it on the matching slot so the
/// job/queue views can serve real step progress instead of a wall-clock guess.
/// </summary>
public interface IStepProgressSink
{
    /// <summary>Record the latest step fraction (0..1) for the render behind a backend prompt id. A frame for a prompt
    /// this instance doesn't own is simply not ours (another client's render, or a slot already finalized) and is
    /// ignored.</summary>
    void ReportStepFraction(string comfyPromptId, double fraction);
}
