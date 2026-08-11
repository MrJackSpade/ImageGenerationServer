using ImageGen.Application.Images;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ImageGen.Tests;

/// <summary>
/// Who may read an image id. There is no cross-user image-viewing feature in this app: every image belongs to the
/// user who generated or uploaded it, and the id-addressed read routes answer nobody else — a signed-in user holding
/// someone else's GUID gets a refusal, not the picture.
/// <para>The check is caller-scoped and reads BOTH ownership records, because neither is total: an image whose
/// history write failed still has its job slot. An id no record places with anyone is refused, including to whoever
/// made it — a default-readable branch is the hole this closes.</para>
/// </summary>
[Collection("db")]
public sealed class ImageVisibilityTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_history_row_makes_the_image_readable_by_that_user_and_nobody_else()
    {
        User alice = await fixture.NewUserAsync("vis-hist-alice");
        User bob = await fixture.NewUserAsync("vis-hist-bob");
        _ = await fixture.History.AddAsync(Entry(alice.Id, "vis-hist-img"), Ct);

        Assert.True(await fixture.ImageVisibility.IsReadableAsync(alice.Id, "vis-hist-img", Ct));
        Assert.False(await fixture.ImageVisibility.IsReadableAsync(bob.Id, "vis-hist-img", Ct));
    }

    /// <summary>The second record of record. A render that produced a real image is never failed for a bad history
    /// write, so the slot can be the only row naming the id — and its owner must still get their picture.</summary>
    [Fact]
    public async Task A_job_slot_makes_the_image_readable_when_no_history_row_was_written()
    {
        User alice = await fixture.NewUserAsync("vis-slot-alice");
        User bob = await fixture.NewUserAsync("vis-slot-bob");
        await AddJobAsync(alice.Id, "vis-slot-img");

        Assert.True(await fixture.ImageVisibility.IsReadableAsync(alice.Id, "vis-slot-img", Ct));
        Assert.False(await fixture.ImageVisibility.IsReadableAsync(bob.Id, "vis-slot-img", Ct));
    }

    /// <summary>An id nothing records is refused. Answering "no record, so allow it" would hand out every legacy and
    /// orphaned image to any signed-in caller who guessed a GUID.</summary>
    [Fact]
    public async Task An_id_no_record_places_with_anyone_is_not_readable()
    {
        User alice = await fixture.NewUserAsync("vis-orphan");

        Assert.False(await fixture.ImageVisibility.IsReadableAsync(alice.Id, "vis-orphan-img", Ct));
    }

    [Fact]
    public async Task The_bulk_filter_keeps_only_the_callers_ids()
    {
        User alice = await fixture.NewUserAsync("vis-bulk-alice");
        User bob = await fixture.NewUserAsync("vis-bulk-bob");
        _ = await fixture.History.AddAsync(Entry(alice.Id, "vis-bulk-mine"), Ct);
        await AddJobAsync(alice.Id, "vis-bulk-mine-slot");
        _ = await fixture.History.AddAsync(Entry(bob.Id, "vis-bulk-theirs"), Ct);

        IReadOnlySet<string> readable = await fixture.ImageVisibility.ReadableAsync(
            alice.Id, ["vis-bulk-mine", "vis-bulk-mine-slot", "vis-bulk-theirs", "vis-bulk-unknown"], Ct);

        Assert.Equal(
            ["vis-bulk-mine", "vis-bulk-mine-slot"],
            [.. readable.OrderBy(x => x, StringComparer.Ordinal)]);
    }

    /// <summary>The bulk query chunks its ids (1000 per statement, inside SQL Server's parameter ceiling). A page can
    /// ask about more than one chunk's worth, so readable ids on BOTH sides of the boundary must come back.</summary>
    [Fact]
    public async Task The_bulk_filter_spans_the_chunk_boundary()
    {
        User user = await fixture.NewUserAsync("vis-chunk");
        List<string> ids = [.. Enumerable.Range(0, 1500).Select(i => $"vis-chunk-{i}")];

        // Readable ids straddling the 1000 boundary; everything else is unknown and must be excluded.
        int[] mine = [3, 500, 999, 1000, 1001, 1400, 1499];
        foreach (int i in mine)
        {
            _ = await fixture.History.AddAsync(Entry(user.Id, ids[i]), Ct);
        }

        IReadOnlySet<string> readable = await fixture.ImageVisibility.ReadableAsync(user.Id, ids, Ct);

        Assert.Equal(
            [.. mine.Select(i => ids[i]).OrderBy(x => x, StringComparer.Ordinal)],
            [.. readable.OrderBy(x => x, StringComparer.Ordinal)]);
    }

    /// <summary>An image id is a caller-controlled route value, so a blank one is garbage input, not a broken
    /// invariant. It must refuse cleanly — no grant — rather than throw and surface as a 500.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_id_is_not_readable_rather_than_throwing(string? imageId)
    {
        InMemoryUploadStore store = new();
        ImageVisibilityService service = new(store, new NoStoredImages());

        Assert.Null(await service.CanReadImageAsync(7, imageId, Ct));
    }

    /// <summary>The grant's constructor is internal to ImageGen.Application and the only assembly given internals
    /// access is this test project — NOT ImageGen.Api. So an endpoint cannot mint a grant; it can only receive one
    /// from <see cref="ImageVisibilityService"/>, which is the whole point of the type.</summary>
    [Fact]
    public void Only_the_application_layer_can_mint_a_grant()
    {
        Assert.Empty(typeof(ImageReadGrant).GetConstructors());   // no PUBLIC constructor exists

        Assembly application = typeof(ImageReadGrant).Assembly;
        string[] friends = [.. application
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName)];

        Assert.Contains("ImageGen.Tests", friends);
        Assert.DoesNotContain("ImageGen.Api", friends);
    }

    [Fact]
    public async Task Asking_about_nothing_is_an_empty_answer_not_a_query()
    {
        User alice = await fixture.NewUserAsync("vis-bulk-empty");

        Assert.Empty(await fixture.ImageVisibility.ReadableAsync(alice.Id, [], Ct));
    }

    /// <summary>An upload is never persisted, so the in-memory store is the ONLY record of its owner — and the editor
    /// serves an upload id back through the same routes as a generated image.</summary>
    [Fact]
    public async Task An_upload_is_readable_by_the_user_who_uploaded_it_and_nobody_else()
    {
        InMemoryUploadStore store = new();
        ImageVisibilityService service = new(store, new NoStoredImages());
        string id = store.Add(new UploadedImage(new byte[8], "image/png", 8, 8, OwnerUserId: 7));

        Assert.NotNull(await service.CanReadImageAsync(7, id, Ct));
        Assert.Null(await service.CanReadImageAsync(8, id, Ct));
    }

    /// <summary>A grant names the id it was issued for, so the load path cannot be handed one image's permission and
    /// another image's id.</summary>
    [Fact]
    public async Task A_grant_carries_the_id_it_was_issued_for()
    {
        InMemoryUploadStore store = new();
        ImageVisibilityService service = new(store, new NoStoredImages());
        string id = store.Add(new UploadedImage(new byte[8], "image/png", 8, 8, OwnerUserId: 7));

        ImageReadGrant? grant = await service.CanReadImageAsync(7, id, Ct);

        Assert.NotNull(grant);
        Assert.Equal(id, grant.ImageId);
    }

    [Fact]
    public async Task The_bulk_filter_keeps_the_callers_uploads_and_drops_another_users()
    {
        InMemoryUploadStore store = new();
        ImageVisibilityService service = new(store, new NoStoredImages());
        string mine = store.Add(new UploadedImage(new byte[8], "image/png", 8, 8, OwnerUserId: 7));
        string theirs = store.Add(new UploadedImage(new byte[8], "image/png", 8, 8, OwnerUserId: 8));

        IReadOnlySet<string> readable = await service.ReadableAsync(7, [mine, theirs], Ct);

        Assert.Contains(mine, readable);
        Assert.DoesNotContain(theirs, readable);
    }

    private async Task AddJobAsync(long userId, string imageId)
    {
        string jobId = Guid.NewGuid().ToString("N");
        await fixture.Jobs.UpsertAsync(new JobRecord
        {
            JobId = jobId,
            UserId = userId,
            MachineName = "BOX-A",
            Model = "sdxl",
            Prompt = "a prompt",
            Total = 1,
            CreatedAtUtc = DateTime.UtcNow,
            Slots =
            [
                new JobSlotRecord
                {
                    JobId = jobId,
                    SlotIndex = 0,
                    State = JobSlotState.Done,
                    ImageId = imageId,
                    Workflow = "test-workflow",
                },
            ],
        }, Ct);
    }

    private static HistoryEntry Entry(long userId, string imageId) => new()
    {
        UserId = userId,
        GatewayImageId = imageId,
        Prompt = "a prompt",
        ModelFriendly = "Test Model",
        ModelId = "test",
        Aspect = "square",
        CreatedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        Marks = [],
    };

    /// <summary>A database with no image anywhere, so the upload half of the resolver is what the assertion sees.</summary>
    private sealed class NoStoredImages : IImageVisibilityRepository
    {
        public Task<bool> IsReadableAsync(long userId, string imageId, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlySet<string>> ReadableAsync(
            long userId, IReadOnlyCollection<string> imageIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }
}
