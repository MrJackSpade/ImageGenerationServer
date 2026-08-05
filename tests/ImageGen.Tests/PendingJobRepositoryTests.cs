using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

[Collection("db")]
public sealed class PendingJobRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Add_then_list_roundtrips()
    {
        User user = await fixture.NewUserAsync("pending-roundtrip");
        await fixture.Pending.AddAsync(Job(user.Id, "job-1"), Ct);

        IReadOnlyList<PendingJob> all = await fixture.Pending.ListAllAsync(Ct);
        List<PendingJob> mine = all.Where(j => j.UserId == user.Id).ToList();
        Assert.Single(mine);
        Assert.Equal("job-1", mine[0].JobId);
        Assert.Equal("a prompt", mine[0].Prompt);
        Assert.Equal("Test Model", mine[0].ModelFriendly);
        Assert.Equal(DateTimeKind.Utc, mine[0].CreatedAtUtc.Kind);
    }

    [Fact]
    public async Task Add_is_idempotent_by_user_and_job_id()
    {
        User user = await fixture.NewUserAsync("pending-dedupe");
        await fixture.Pending.AddAsync(Job(user.Id, "dup"), Ct);
        await fixture.Pending.AddAsync(Job(user.Id, "dup"), Ct);

        int mine = (await fixture.Pending.ListAllAsync(Ct)).Count(j => j.UserId == user.Id);
        Assert.Equal(1, mine);
    }

    [Fact]
    public async Task Remove_clears_only_the_target_row()
    {
        User user = await fixture.NewUserAsync("pending-remove");
        await fixture.Pending.AddAsync(Job(user.Id, "keep"), Ct);
        await fixture.Pending.AddAsync(Job(user.Id, "drop"), Ct);

        PendingJob drop = (await fixture.Pending.ListAllAsync(Ct)).Single(j => j.UserId == user.Id && j.JobId == "drop");
        await fixture.Pending.RemoveAsync(drop.Id, Ct);

        List<string> mine = (await fixture.Pending.ListAllAsync(Ct)).Where(j => j.UserId == user.Id).Select(j => j.JobId).ToList();
        Assert.Equal(["keep"], mine);
    }

    [Fact]
    public async Task ListForUser_returns_only_that_users_jobs_oldest_first()
    {
        User alice = await fixture.NewUserAsync("pending-alice");
        User bob = await fixture.NewUserAsync("pending-bob");
        await fixture.Pending.AddAsync(Job(alice.Id, "a1", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)), Ct);
        await fixture.Pending.AddAsync(Job(alice.Id, "a2", new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)), Ct);
        await fixture.Pending.AddAsync(Job(bob.Id, "b1"), Ct);

        IReadOnlyList<PendingJob> mine = await fixture.Pending.ListForUserAsync(alice.Id, Ct);
        Assert.Equal(["a1", "a2"], mine.Select(j => j.JobId).ToList());   // oldest first, bob's excluded
    }

    private static PendingJob Job(long userId, string jobId, DateTime? createdAtUtc = null) => new()
    {
        UserId = userId,
        JobId = jobId,
        Prompt = "a prompt",
        ModelFriendly = "Test Model",
        ModelId = "test",
        Aspect = "square",
        CreatedAtUtc = createdAtUtc ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
    };
}
