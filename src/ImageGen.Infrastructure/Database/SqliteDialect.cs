namespace ImageGen.Infrastructure.Database;

/// <summary>
/// <see cref="ISqlDialect"/> for SQLite. The <c>dbo.</c> prefixes below are real, not leftovers: the connection
/// attaches the database file under that name (see <see cref="SqliteConnectionFactory"/>), which is what lets the
/// rest of the app's SQL stay provider-agnostic.
/// </summary>
public sealed class SqliteDialect : ISqlDialect
{
    /// <inheritdoc />
    public string Paginate(string skipParameter, string takeParameter) =>
        $"LIMIT {takeParameter} OFFSET {skipParameter}";

    /// <inheritdoc />
    public string TopPrefix(string takeParameter) => "";

    /// <inheritdoc />
    public string TopSuffix(string takeParameter) => $" LIMIT {takeParameter}";

    /// <summary>
    /// <inheritdoc cref="ISqlDialect.InsertedIdentityOrNull" />
    /// <para>The <c>changes() = 0</c> guard is the entire point. <c>last_insert_rowid()</c> on its own reports the id
    /// of the previous successful insert on this connection, so a guarded insert that matched an existing row would
    /// come back with a real-looking id and the caller would report a duplicate registration as a brand new account.
    /// Both halves of that are proved in <c>SqliteAttachSpikeTests</c>.</para>
    /// </summary>
    public string InsertedIdentityOrNull =>
        "SELECT CASE WHEN changes() = 0 THEN NULL ELSE last_insert_rowid() END;";

    /// <inheritdoc />
    public string UpsertJob => @"
INSERT INTO dbo.Job (JobId, UserId, MachineName, Model, Prompt, Total, Status, CreatedAtUtc, FinishedAtUtc)
VALUES (@jobId, @userId, @machine, @model, @prompt, @total, @status, @created, @finished)
ON CONFLICT (JobId) DO UPDATE SET
    Model = @model, Prompt = @prompt, Total = @total, Status = @status, FinishedAtUtc = @finished;";

    /// <inheritdoc />
    public string UpsertJobSlot => @"
INSERT INTO dbo.JobSlot
    (JobId, SlotIndex, IsEdit, IsBackground, State, ComfyPromptId, ImageId, Width, Height, Changed, ChangeScore, Error,
     EffectivePrompt, ModelPrompt, ModelManifestJson, RenderDimensionsJson, RawPrompt, RawNegativePrompt, GenStartedAtUtc, ExpectedGenSeconds,
     Workflow, Prompt, NegativePrompt, Aspect, RandomArtist, RandomPrompt, Temperature, TagTypesJson,
     OverridesJson, LorasJson, SourceImageId, MaskImageId, LastFrameImageId)
VALUES (@jobId, @idx, @isEdit, @isBackground, @state, @comfy, @imageId, @width, @height, @changed, @score, @error,
        @effective, @modelPrompt, @modelManifest, @renderDimensions, @raw, @rawNeg, @started, @expected,
        @workflow, @specPrompt, @specNegative, @aspect, @randomArtist, @randomPrompt, @temperature, @tagTypes,
        @overrides, @loras, @source, @mask, @lastFrame)
ON CONFLICT (JobId, SlotIndex) DO UPDATE SET
    IsEdit = @isEdit, IsBackground = @isBackground, State = @state, ComfyPromptId = @comfy, ImageId = @imageId, Width = @width, Height = @height,
    Changed = @changed, ChangeScore = @score, Error = @error, EffectivePrompt = @effective, ModelPrompt = @modelPrompt,
    ModelManifestJson = @modelManifest, RenderDimensionsJson = @renderDimensions, RawPrompt = @raw,
    RawNegativePrompt = @rawNeg, GenStartedAtUtc = @started, ExpectedGenSeconds = @expected,
    Workflow = @workflow, Prompt = @specPrompt, NegativePrompt = @specNegative, Aspect = @aspect,
    RandomArtist = @randomArtist, RandomPrompt = @randomPrompt, Temperature = @temperature,
    TagTypesJson = @tagTypes, OverridesJson = @overrides, LorasJson = @loras, SourceImageId = @source, MaskImageId = @mask,
    LastFrameImageId = @lastFrame;";
}
