using ImageGen.Domain;
using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

/// <summary>
/// The slot's render spec, stored as TYPED COLUMNS and child rows rather than one encrypted JSON blob.
///
/// <para>These replace <c>RequestJsonContractTests</c>, which existed because the blob was a serialization contract:
/// renaming a property still deserialized, handing back an object with a hole in it, and a job sat Active for five
/// weeks because its workflow arrived null. A renamed column cannot do that — it fails at the database. What is worth
/// pinning now is different: that every field survives the round trip, that the FOREIGN KEYS are legible (plain, so
/// they can be joined and counted, which is the whole point), and that the user's text is not.</para>
/// </summary>
[Collection("db")]
public sealed class JobSlotSpecTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>A generate slot's every field comes back as it went in.</summary>
    [Fact]
    public async Task A_generate_spec_round_trips_through_typed_columns()
    {
        var user = await fixture.NewUserAsync("slot-gen-spec");
        var jobId = Guid.NewGuid().ToString("N");
        var slot = new JobSlotRecord
        {
            JobId = jobId,
            SlotIndex = 0,
            State = JobSlotState.Queued,
            Workflow = "anima",
            Prompt = "1girl, #long_hair, @monet",
            NegativePrompt = "worst quality",
            Aspect = "portrait",
            RandomArtist = true,
            RandomPrompt = false,
            Temperature = 0.85,
            TagTypesJson = """["character","meta"]""",
            OverridesJson = """{"seed":1234,"steps":28}""",
            LorasJson = """[{"Name":"anime/foo.safetensors","Weight":0.8}]""",
        };

        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, [slot]), Ct);
        var job = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(job);
        var back = job.Slots.Single();

        Assert.Equal("anima", back.Workflow);
        Assert.Equal("1girl, #long_hair, @monet", back.Prompt);
        Assert.Equal("worst quality", back.NegativePrompt);
        Assert.Equal("portrait", back.Aspect);
        Assert.True(back.RandomArtist);
        Assert.False(back.RandomPrompt);
        Assert.Equal(0.85, back.Temperature);
        Assert.Equal("""["character","meta"]""", back.TagTypesJson);
        Assert.Equal("""{"seed":1234,"steps":28}""", back.OverridesJson);
        Assert.Equal("""[{"Name":"anime/foo.safetensors","Weight":0.8}]""", back.LorasJson);
    }

    /// <summary>
    /// An edit's four image ids — source, mask, end frame, and its ordered references — survive as ids. They used to
    /// live inside the encrypted blob, where nothing could join or count them; that is how 19,329 upload rows became
    /// unreachable. Order matters for the references: they are positional to the workflow.
    /// </summary>
    [Fact]
    public async Task An_edits_image_ids_round_trip_and_references_keep_their_order()
    {
        var user = await fixture.NewUserAsync("slot-edit-spec");
        var jobId = Guid.NewGuid().ToString("N");
        var slot = new JobSlotRecord
        {
            JobId = jobId,
            SlotIndex = 0,
            IsEdit = true,
            State = JobSlotState.Queued,
            Workflow = "anima-inpaint",
            Prompt = "make it night",
            SourceImageId = "src-1",
            MaskImageId = "mask-1",
            LastFrameImageId = "last-1",
            ReferenceImageIds = ["ref-a", "ref-b", "ref-c"],
        };

        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId, [slot]), Ct);
        var job = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(job);
        var back = job.Slots.Single();

        Assert.Equal("src-1", back.SourceImageId);
        Assert.Equal("mask-1", back.MaskImageId);
        Assert.Equal("last-1", back.LastFrameImageId);
        Assert.Equal(["ref-a", "ref-b", "ref-c"], back.ReferenceImageIds);
    }

    /// <summary>
    /// The image ids are stored PLAIN, so a query can find every slot that used an image without a key. This is the
    /// property the whole change exists for: a foreign key inside an encrypted blob is not a foreign key.
    /// </summary>
    [Fact]
    public async Task An_images_uses_can_be_found_by_query()
    {
        var user = await fixture.NewUserAsync("slot-joinable");
        var jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId,
        [
            new JobSlotRecord
            {
                JobId = jobId, SlotIndex = 0, IsEdit = true, State = JobSlotState.Queued,
                Workflow = "anima-inpaint", SourceImageId = "shared-input", ReferenceImageIds = ["shared-input"],
            },
        ]), Ct);

        await using var conn = await fixture.ConnectionFactory.OpenAsync(Ct);
        await using var cmd = conn.Command(
            "SELECT (SELECT COUNT(*) FROM dbo.JobSlot WHERE SourceImageId = @img)" +
            "     + (SELECT COUNT(*) FROM dbo.JobSlotReference WHERE ImageId = @img);");
        cmd.AddParam("@img", "shared-input");

        Assert.Equal(2, Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct)));
    }

    /// <summary>The user's TEXT is still protected: the prompt column holds ciphertext, and the workflow beside it
    /// does not. Encryption is a property of a field, which is the other half of the point.</summary>
    [Fact]
    public async Task The_prompt_is_encrypted_at_rest_and_the_workflow_is_not()
    {
        var user = await fixture.NewUserAsync("slot-field-crypto");
        var jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId,
        [
            new JobSlotRecord
            {
                JobId = jobId, SlotIndex = 0, State = JobSlotState.Queued,
                Workflow = "anima", Prompt = "a very distinctive prompt",
            },
        ]), Ct);

        await using var conn = await fixture.ConnectionFactory.OpenAsync(Ct);
        await using var cmd = conn.Command(
            "SELECT Prompt, Workflow FROM dbo.JobSlot WHERE JobId = @jobId;");
        cmd.AddParam("@jobId", jobId);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));

        Assert.NotEqual("a very distinctive prompt", reader.GetString(0));   // ciphertext at rest
        Assert.Equal("anima", reader.GetString(1));                          // plain, so it can be queried
    }

    /// <summary>
    /// Marks are rows (dbo.JobSlotMark), mirroring dbo.HistoryMark: deterministically encrypted so equality still
    /// works over them, and gone from the blob that nothing could query.
    /// </summary>
    [Fact]
    public async Task Slot_marks_round_trip_as_rows()
    {
        var user = await fixture.NewUserAsync("slot-marks");
        var jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(Job(user.Id, jobId,
        [
            new JobSlotRecord
            {
                JobId = jobId, SlotIndex = 0, State = JobSlotState.Done, ImageId = "img-1", Workflow = "anima",
                Marks = [new Mark("long_hair", TokenKind.Tag), new Mark("monet", TokenKind.Artist)],
            },
        ]), Ct);

        var job = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(job);
        var back = job.Slots.Single();

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
        var user = await fixture.NewUserAsync("slot-children-replace");
        var jobId = Guid.NewGuid().ToString("N");

        JobRecord WithReferences(params string[] refs) => Job(user.Id, jobId,
        [
            new JobSlotRecord
            {
                JobId = jobId, SlotIndex = 0, IsEdit = true, State = JobSlotState.Queued, Workflow = "anima-inpaint",
                SourceImageId = "src", ReferenceImageIds = [.. refs],
                Marks = [new Mark("long_hair", TokenKind.Tag)],
            },
        ]);

        await fixture.Jobs.UpsertAsync(WithReferences("a", "b", "c"), Ct);
        await fixture.Jobs.UpsertAsync(WithReferences("a"), Ct);

        var job = await fixture.Jobs.GetAsync(jobId, Ct);
        Assert.NotNull(job);
        var back = job.Slots.Single();
        Assert.Equal(["a"], back.ReferenceImageIds);
        Assert.Single(back.Marks);
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
