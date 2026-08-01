namespace ImageGen.Infrastructure.Database;

/// <summary>
/// <see cref="ISqlDialect"/> for SQL Server. Every member here is the wording the app shipped with, moved behind the
/// interface unchanged — so introducing the second provider could not alter what runs against the existing database.
/// </summary>
public sealed class SqlServerDialect : ISqlDialect
{
    /// <inheritdoc />
    public string Paginate(string skipParameter, string takeParameter) =>
        $"OFFSET {skipParameter} ROWS FETCH NEXT {takeParameter} ROWS ONLY";

    /// <inheritdoc />
    public string TopPrefix(string takeParameter) => $"TOP ({takeParameter}) ";

    /// <inheritdoc />
    public string TopSuffix(string takeParameter) => "";

    /// <inheritdoc />
    public string InsertedIdentityOrNull => "SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

    /// <inheritdoc />
    public string UpsertJob => @"
MERGE dbo.Job AS t
USING (SELECT @jobId AS JobId) AS s ON t.JobId = s.JobId
WHEN MATCHED THEN UPDATE SET
    Model = @model, Prompt = @prompt, Total = @total, Status = @status, FinishedAtUtc = @finished
WHEN NOT MATCHED THEN
    INSERT (JobId, UserId, MachineName, Model, Prompt, Total, Status, CreatedAtUtc, FinishedAtUtc)
    VALUES (@jobId, @userId, @machine, @model, @prompt, @total, @status, @created, @finished);";

    /// <inheritdoc />
    public string UpsertJobSlot => @"
MERGE dbo.JobSlot AS t
USING (SELECT @jobId AS JobId, @idx AS SlotIndex) AS s ON t.JobId = s.JobId AND t.SlotIndex = s.SlotIndex
WHEN MATCHED THEN UPDATE SET
    IsEdit = @isEdit, State = @state, ComfyPromptId = @comfy, ImageId = @imageId, Width = @width, Height = @height,
    Changed = @changed, ChangeScore = @score, Error = @error, EffectivePrompt = @effective, RawPrompt = @raw,
    RawNegativePrompt = @rawNeg, GenStartedAtUtc = @started, ExpectedGenSeconds = @expected,
    Workflow = @workflow, Prompt = @specPrompt, NegativePrompt = @specNegative, Aspect = @aspect,
    RandomArtist = @randomArtist, RandomPrompt = @randomPrompt, Temperature = @temperature,
    TagTypesJson = @tagTypes, OverridesJson = @overrides, SourceImageId = @source, MaskImageId = @mask,
    LastFrameImageId = @lastFrame
WHEN NOT MATCHED THEN
    INSERT (JobId, SlotIndex, IsEdit, State, ComfyPromptId, ImageId, Width, Height, Changed, ChangeScore, Error,
            EffectivePrompt, RawPrompt, RawNegativePrompt, GenStartedAtUtc, ExpectedGenSeconds,
            Workflow, Prompt, NegativePrompt, Aspect, RandomArtist, RandomPrompt, Temperature, TagTypesJson,
            OverridesJson, SourceImageId, MaskImageId, LastFrameImageId)
    VALUES (@jobId, @idx, @isEdit, @state, @comfy, @imageId, @width, @height, @changed, @score, @error,
            @effective, @raw, @rawNeg, @started, @expected,
            @workflow, @specPrompt, @specNegative, @aspect, @randomArtist, @randomPrompt, @temperature, @tagTypes,
            @overrides, @source, @mask, @lastFrame);";
}
