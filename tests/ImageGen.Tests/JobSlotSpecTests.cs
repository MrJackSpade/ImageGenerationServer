using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using System.Data.Common;
using System.Text.Json;

namespace ImageGen.Tests;

/// <summary>
/// The slot's render spec, stored as TYPED COLUMNS and child rows rather than one encrypted JSON blob.
///
/// <para>Stored this way, a renamed field fails at the database. A single encrypted JSON blob would not: a renamed
/// property still deserializes, handing back an object with a hole in it, and a job whose workflow arrived null
/// would sit Active forever. What is worth pinning is that every field survives the round trip, that the FOREIGN
/// KEYS are legible (plain, so they can be joined and counted, which is the whole point), and that the user's text
/// is not.</para>
/// </summary>
[Collection("db")]
public sealed class JobSlotSpecTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>A generate slot's every field comes back as it went in.</summary>
    [Fact]
    public async Task A_generate_spec_round_trips_through_typed_columns()
    {
        User user = await fixture.NewUserAsync("slot-gen-spec");
        string jobId = Guid.NewGuid().ToString("N");
        JobSlotRecord slot = new()
        {
            JobId = jobId,
            SlotIndex = 0,
            State = JobSlotState.Queued,
            Workflow = "anima",
            Prompt = "1girl, #long_hair, @monet",
            NegativePrompt = "worst quality",
            OverridesJson = """{"seed":1234,"steps":28}""",
            LorasJson = """[{"Name":"anime/foo.safetensors","Weight":0.8}]""",
            ModelManifestJson = """{"checkpoint":"anima.safetensors","loader":"checkpoint","weightDtype":"default","quantization":"unknown","vae":null,"textEncoders":[]}""",
            Generate = new GenerateSlotData
            {
                Aspect = "portrait",
                RandomArtist = TriState.True,
                RandomPrompt = TriState.False,
                Temperature = 0.85,
                TagTypesJson = """["character","meta"]""",
            },
        };

        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, [slot]), Ct);
        JobRecord? job = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(job);
        JobSlotRecord back = job.Slots.Single();

        Assert.Equal("anima", back.Workflow);
        Assert.Equal("1girl, #long_hair, @monet", back.Prompt);
        Assert.Equal("worst quality", back.NegativePrompt);
        Assert.Equal("""{"seed":1234,"steps":28}""", back.OverridesJson);
        Assert.Equal("""[{"Name":"anime/foo.safetensors","Weight":0.8}]""", back.LorasJson);
        Assert.Contains("anima.safetensors", back.ModelManifestJson);
        Assert.Null(back.Edit);                        // a generate carries no edit data
        Assert.NotNull(back.Generate);
        Assert.Equal("portrait", back.Generate.Aspect);
        Assert.Equal(TriState.True, back.Generate.RandomArtist);
        Assert.Equal(TriState.False, back.Generate.RandomPrompt);
        Assert.Equal(0.85, back.Generate.Temperature);
        Assert.Equal("""["character","meta"]""", back.Generate.TagTypesJson);
    }

    /// <summary>The generation-values payload distinguishes what the user requested from the exact positive text the
    /// workflow submitted after applying its prompt template.</summary>
    [Fact]
    public async Task Image_request_values_include_the_exact_model_prompt()
    {
        User user = await fixture.NewUserAsync("slot-model-prompt");
        string jobId = Guid.NewGuid().ToString("N");
        const string imageId = "model-prompt-image";
        const string requested = "a lighthouse at dusk";
        const string displayed = "a lighthouse at dusk";
        const string submitted = "{\"high_level_description\":\"a lighthouse at dusk\"}";
        JobSlotRecord slot = new()
        {
            JobId = jobId,
            SlotIndex = 0,
            State = JobSlotState.Done,
            ImageId = imageId,
            Workflow = "ideogram4",
            Prompt = requested,
            EffectivePrompt = displayed,
            ModelPrompt = submitted,
            ModelManifestJson = """{"checkpoint":"ideogram4-fp8.safetensors","loader":"unet","weightDtype":"default","quantization":"fp8","vae":"ae.safetensors","textEncoders":["gemma.safetensors"]}""",
            RenderDimensionsJson = """{"policy":"explicit-requested","input":null,"working":{"width":1024,"height":1024},"output":{"width":1024,"height":1024}}""",
            Generate = new GenerateSlotData { Aspect = "square" },
        };

        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, [slot]), Ct);
        JobRecord? job = await fixture.Jobs.GetAsync(jobId, Ct);
        ImageRequestRecord? record = await fixture.Jobs.GetRequestByImageAsync(imageId, Ct);

        Assert.NotNull(job);
        Assert.Equal(displayed, Assert.Single(job.Slots).EffectivePrompt);
        Assert.Equal(submitted, Assert.Single(job.Slots).ModelPrompt);
        Assert.NotNull(record);
        using JsonDocument values = JsonDocument.Parse(record.RequestJson);
        Assert.Equal(submitted, values.RootElement.GetProperty("prompt").GetString());
        JsonElement models = values.RootElement.GetProperty("models");
        Assert.Equal("ideogram4-fp8.safetensors", models.GetProperty("checkpoint").GetString());
        Assert.Equal("fp8", models.GetProperty("quantization").GetString());
        JsonElement dimensions = values.RootElement.GetProperty("dimensions");
        Assert.Equal("explicit-requested", dimensions.GetProperty("policy").GetString());
        Assert.Equal(1024, dimensions.GetProperty("working").GetProperty("width").GetInt32());
        Assert.Equal(1024, dimensions.GetProperty("output").GetProperty("height").GetInt32());
        Assert.False(values.RootElement.TryGetProperty("modelPrompt", out _));
    }

    /// <summary>
    /// An edit's four image ids — source, mask, end frame, and its ordered references — survive as ids. Inside an
    /// encrypted blob nothing could join or count them, leaving upload rows unreachable. Order matters for the
    /// references: they are positional to the workflow.
    /// </summary>
    [Fact]
    public async Task An_edits_image_ids_round_trip_and_references_keep_their_order()
    {
        User user = await fixture.NewUserAsync("slot-edit-spec");
        string jobId = Guid.NewGuid().ToString("N");
        JobSlotRecord slot = new()
        {
            JobId = jobId,
            SlotIndex = 0,
            IsEdit = true,
            State = JobSlotState.Queued,
            Workflow = "anima-inpaint",
            Prompt = "make it night",
            Edit = new EditSlotData
            {
                SourceImageId = "src-1",
                MaskImageId = "mask-1",
                LastFrameImageId = "last-1",
                ReferenceIds = ["ref-a", "ref-b", "ref-c"],
            },
        };

        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, [slot]), Ct);
        JobRecord? job = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(job);
        JobSlotRecord back = job.Slots.Single();

        Assert.NotNull(back.Edit);
        Assert.Equal("src-1", back.Edit.SourceImageId);
        Assert.Equal("mask-1", back.Edit.MaskImageId);
        Assert.Equal("last-1", back.Edit.LastFrameImageId);
        Assert.Equal(["ref-a", "ref-b", "ref-c"], back.Edit.ReferenceIds);
        // An edit carries no generate data at all — the generate-only fields aren't null-on-an-edit, they're absent.
        Assert.Null(back.Generate);
    }

    /// <summary>
    /// The image ids are stored PLAIN, so a query can find every slot that used an image without a key. This is the
    /// property the whole change exists for: a foreign key inside an encrypted blob is not a foreign key.
    /// </summary>
    [Fact]
    public async Task An_images_uses_can_be_found_by_query()
    {
        User user = await fixture.NewUserAsync("slot-joinable");
        string jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId,
        [
            new JobSlotRecord
            {
                JobId = jobId, SlotIndex = 0, IsEdit = true, State = JobSlotState.Queued, Workflow = "anima-inpaint",
                Edit = new EditSlotData { SourceImageId = "shared-input", ReferenceIds = ["shared-input"] },
            },
        ]), Ct);

        await using DbConnection conn = await fixture.ConnectionFactory.OpenAsync(Ct);
        await using DbCommand cmd = conn.Command(
            "SELECT (SELECT COUNT(*) FROM dbo.JobSlot WHERE SourceImageId = @img)" +
            "     + (SELECT COUNT(*) FROM dbo.JobSlotReference WHERE ImageId = @img);");
        _ = cmd.AddParam("@img", "shared-input");

        Assert.Equal(2, Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct)));
    }

    /// <summary>The user's TEXT is still protected: the prompt column holds ciphertext, and the workflow beside it
    /// does not. Encryption is a property of a field, which is the other half of the point.</summary>
    [Fact]
    public async Task The_prompt_is_encrypted_at_rest_and_the_workflow_is_not()
    {
        User user = await fixture.NewUserAsync("slot-field-crypto");
        string jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId,
        [
            new JobSlotRecord
            {
                JobId = jobId, SlotIndex = 0, State = JobSlotState.Queued,
                Workflow = "anima", Prompt = "a very distinctive prompt",
                ModelPrompt = "{\"description\":\"a very distinctive prompt\"}",
            },
        ]), Ct);

        await using DbConnection conn = await fixture.ConnectionFactory.OpenAsync(Ct);
        await using DbCommand cmd = conn.Command(
            "SELECT Prompt, Workflow, ModelPrompt FROM dbo.JobSlot WHERE JobId = @jobId;");
        _ = cmd.AddParam("@jobId", jobId);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));

        Assert.NotEqual("a very distinctive prompt", reader.GetString(0));   // ciphertext at rest
        Assert.Equal("anima", reader.GetString(1));                          // plain, so it can be queried
        Assert.DoesNotContain("a very distinctive prompt", reader.GetString(2)); // exact model prompt is protected too
    }

    /// <summary>
    /// Marks are rows (dbo.JobSlotMark), mirroring dbo.HistoryMark: deterministically encrypted so equality still
    /// works over them, and gone from the blob that nothing could query.
    /// </summary>
    [Fact]
    public async Task Slot_marks_round_trip_as_rows()
    {
        User user = await fixture.NewUserAsync("slot-marks");
        string jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId,
        [
            new JobSlotRecord
            {
                JobId = jobId, SlotIndex = 0, State = JobSlotState.Done, ImageId = "img-1", Workflow = "anima",
                Marks = [new Mark("long_hair", TokenKind.Tag), new Mark("monet", TokenKind.Artist)],
            },
        ]), Ct);

        JobRecord? job = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(job);
        JobSlotRecord back = job.Slots.Single();

        Assert.Equal(2, back.Marks.Count);
        Assert.Contains(back.Marks, m => m is { Token: "long_hair", Kind: TokenKind.Tag });
        Assert.Contains(back.Marks, m => m is { Token: "monet", Kind: TokenKind.Artist });
    }

    /// <summary>
    /// A slot is written through on EVERY state transition, so the child rows have to be replaced, not accumulated:
    /// a reference list that shrank must not leave the dropped rows behind, and re-writing the same marks must not
    /// duplicate them.
    /// </summary>
    [Fact]
    public async Task Re_upserting_a_slot_replaces_its_children_rather_than_accumulating_them()
    {
        User user = await fixture.NewUserAsync("slot-children-replace");
        string jobId = Guid.NewGuid().ToString("N");

        JobRecord WithReferences(params string[] refs)
        {
            return Job(user.Id, jobId,
        [
            new JobSlotRecord
            {
                JobId = jobId, SlotIndex = 0, IsEdit = true, State = JobSlotState.Queued, Workflow = "anima-inpaint",
                Edit = new EditSlotData { SourceImageId = "src", ReferenceIds = [.. refs] },
                Marks = [new Mark("long_hair", TokenKind.Tag)],
            },
        ]);
        }

        await fixture.Jobs.UpsertAsync(WithReferences("a", "b", "c"), Ct);
        await fixture.Jobs.UpsertAsync(WithReferences("a"), Ct);

        JobRecord? job = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(job);
        JobSlotRecord back = job.Slots.Single();
        Assert.NotNull(back.Edit);
        Assert.Equal(["a"], back.Edit.ReferenceIds);
        _ = Assert.Single(back.Marks);
    }

    private static JobRecord Job(long userId, string jobId, List<JobSlotRecord> slots) => new()
    {
        JobId = jobId,
        UserId = userId,
        MachineName = "BOX-A",
        Model = "anima",
        Prompt = "a prompt",
        Total = slots.Count,
        CreatedAtUtc = DateTime.UtcNow,
        Slots = slots,
    };
}
