//TODO: CHECK FOR FALLBACKS
using ImageGen.Application.Services;
using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

/// <summary>
/// The Recent strip's window is decided SERVER-side: the newest `min` images, stretched to cover the user's
/// current-or-last batch whenever that batch produced more. It used to be assembled in the browser from live job
/// events, so the size existed only in a tab that watched the batch happen — reload after it finished and the strip
/// cropped the last batch to `min` (50 made, 48 shown). These pin the rule where it now lives.
/// </summary>
[Collection("db")]
public sealed class RecentsWindowTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Noon = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    private HistoryService Service => new(fixture.History, fixture.ImageDeletions, fixture.Jobs);

    /// <summary>A batch bigger than the minimum stretches the window to the whole batch — the bug, in one test.</summary>
    [Fact]
    public async Task A_batch_bigger_than_the_minimum_is_shown_whole()
    {
        var user = await fixture.NewUserAsync("recents-big-batch");
        await SeedHistoryAsync(user.Id, 14);
        await SeedLatestBatchAsync(user.Id, produced: 10);

        var items = await Service.GetRecentsAsync(user.Id, minimum: 6, ct: Ct);

        Assert.Equal(10, items.Count);
        Assert.Equal("img-13", items[0].GatewayImageId);   // newest first
    }

    /// <summary>A batch smaller than the minimum leaves the strip at the minimum, padded with what came before it.</summary>
    [Fact]
    public async Task A_small_batch_leaves_the_minimum_standing()
    {
        var user = await fixture.NewUserAsync("recents-small-batch");
        await SeedHistoryAsync(user.Id, 14);
        await SeedLatestBatchAsync(user.Id, produced: 2);

        var items = await Service.GetRecentsAsync(user.Id, minimum: 6, ct: Ct);

        Assert.Equal(6, items.Count);
    }

    /// <summary>A user who has never generated gets whatever history they have, and no error.</summary>
    [Fact]
    public async Task No_batch_at_all_falls_back_to_the_minimum()
    {
        var user = await fixture.NewUserAsync("recents-no-batch");
        await SeedHistoryAsync(user.Id, 3);

        var items = await Service.GetRecentsAsync(user.Id, minimum: 6, ct: Ct);

        Assert.Equal(3, items.Count);   // history ran out — the window is a ceiling, not a promise
    }

    /// <summary>
    /// Sizing keys on what the batch PRODUCED, not what it will eventually make: a half-done run of 10 that has made 3
    /// must not stretch the window to 10, which would fill the gap with images from before it started.
    /// </summary>
    [Fact]
    public async Task A_half_done_batch_sizes_to_what_it_has_made()
    {
        var user = await fixture.NewUserAsync("recents-half-done");
        await SeedHistoryAsync(user.Id, 20);
        await SeedLatestBatchAsync(user.Id, produced: 3, queued: 7);

        var items = await Service.GetRecentsAsync(user.Id, minimum: 4, ct: Ct);

        Assert.Equal(4, items.Count);   // max(min 4, produced 3) — not 10
    }

    private async Task SeedHistoryAsync(long userId, int count)
    {
        for (var i = 0; i < count; i++)
            await fixture.History.AddAsync(new HistoryEntry
            {
                UserId = userId,
                GatewayImageId = $"img-{i}",
                Prompt = "a prompt",
                ModelFriendly = "Test Model",
                ModelId = "test",
                Aspect = "square",
                CreatedAtUtc = Noon.AddMinutes(i),
                Marks = [],
            }, Ct);
    }

    private async Task SeedLatestBatchAsync(long userId, int produced, int queued = 0)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var slots = new List<JobSlotRecord>();
        for (var i = 0; i < produced + queued; i++)
            slots.Add(new JobSlotRecord
            {
                JobId = jobId,
                SlotIndex = i,
                State = i < produced ? JobSlotState.Done : JobSlotState.Queued,
                ImageId = i < produced ? $"img-{i}" : null,
                Workflow = "test-workflow",
            });

        await fixture.Jobs.UpsertAsync(new JobRecord
        {
            JobId = jobId,
            UserId = userId,
            MachineName = "BOX-A",
            Model = "test",
            Prompt = "a prompt",
            Total = slots.Count,
            CreatedAtUtc = Noon.AddHours(1),
            Slots = slots,
        }, Ct);
    }
}
