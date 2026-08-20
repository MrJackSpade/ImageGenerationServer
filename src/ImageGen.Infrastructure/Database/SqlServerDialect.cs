namespace ImageGen.Infrastructure.Database;

/// <summary>
/// <see cref="ISqlDialect"/> for SQL Server. Every member here is the canonical SQL Server wording, kept behind the
/// interface unchanged so the second provider cannot alter what runs against a SQL Server database.
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
MERGE dbo.Job WITH (HOLDLOCK) AS t
USING (SELECT @jobId AS JobId) AS s ON t.JobId = s.JobId
WHEN MATCHED THEN UPDATE SET
    Model = @model, Prompt = @prompt, Total = @total, Status = @status, FinishedAtUtc = @finished
WHEN NOT MATCHED THEN
    INSERT (JobId, UserId, MachineName, Model, Prompt, Total, Status, CreatedAtUtc, FinishedAtUtc)
    VALUES (@jobId, @userId, @machine, @model, @prompt, @total, @status, @created, @finished);";

    /// <inheritdoc />
    public string UpsertJobSlot => @"
MERGE dbo.JobSlot WITH (HOLDLOCK) AS t
USING (SELECT @jobId AS JobId, @idx AS SlotIndex) AS s ON t.JobId = s.JobId AND t.SlotIndex = s.SlotIndex
WHEN MATCHED THEN UPDATE SET
    IsEdit = @isEdit, IsBackground = @isBackground, State = @state, ComfyPromptId = @comfy, ImageId = @imageId, Width = @width, Height = @height,
    Changed = @changed, ChangeScore = @score, Error = @error, EffectivePrompt = @effective, ModelPrompt = @modelPrompt,
    ModelManifestJson = @modelManifest, RenderDimensionsJson = @renderDimensions, RawPrompt = @raw,
    RawNegativePrompt = @rawNeg, GenStartedAtUtc = @started, ExpectedGenSeconds = @expected,
    Workflow = @workflow, Prompt = @specPrompt, NegativePrompt = @specNegative, Aspect = @aspect,
    RandomArtist = @randomArtist, RandomPrompt = @randomPrompt, Temperature = @temperature,
    TagTypesJson = @tagTypes, OverridesJson = @overrides, LorasJson = @loras, SourceImageId = @source, MaskImageId = @mask,
    LastFrameImageId = @lastFrame
WHEN NOT MATCHED THEN
    INSERT (JobId, SlotIndex, IsEdit, IsBackground, State, ComfyPromptId, ImageId, Width, Height, Changed, ChangeScore, Error,
            EffectivePrompt, ModelPrompt, ModelManifestJson, RenderDimensionsJson, RawPrompt, RawNegativePrompt, GenStartedAtUtc, ExpectedGenSeconds,
            Workflow, Prompt, NegativePrompt, Aspect, RandomArtist, RandomPrompt, Temperature, TagTypesJson,
            OverridesJson, LorasJson, SourceImageId, MaskImageId, LastFrameImageId)
    VALUES (@jobId, @idx, @isEdit, @isBackground, @state, @comfy, @imageId, @width, @height, @changed, @score, @error,
            @effective, @modelPrompt, @modelManifest, @renderDimensions, @raw, @rawNeg, @started, @expected,
            @workflow, @specPrompt, @specNegative, @aspect, @randomArtist, @randomPrompt, @temperature, @tagTypes,
            @overrides, @loras, @source, @mask, @lastFrame);";
}
