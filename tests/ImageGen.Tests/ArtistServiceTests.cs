using ImageGen.Application.Models;
using ImageGen.Application.Services;
using ImageGen.Domain;
using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

[Collection("db")]
public sealed class ArtistServiceTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private ArtistService NewService() => new(fixture.ArtistDisplays, fixture.History);

    [Fact]
    public async Task Display_falls_back_to_latest_gen_then_honors_an_override()
    {
        User user = await fixture.NewUserAsync("artist-display");
        // Two gens for "monet"; the newer one is the fallback display image.
        await fixture.History.AddAsync(Gen(user.Id, "monet-old", "monet", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)), Ct);
        await fixture.History.AddAsync(Gen(user.Id, "monet-new", "monet", new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc)), Ct);
        ArtistService svc = NewService();

        ArtistDisplayResult fallback = await svc.GetDisplayAsync(user.Id, "monet", Ct);
        Assert.Equal(new ArtistDisplayResult("monet-new", false), fallback);

        // Pick the older one explicitly -> override wins and is flagged as such.
        Assert.True(await svc.SetAsync(user.Id, "monet", "monet-old", DateTime.UtcNow, Ct));
        ArtistDisplayResult chosen = await svc.GetDisplayAsync(user.Id, "monet", Ct);
        Assert.Equal(new ArtistDisplayResult("monet-old", true), chosen);

        // Clearing reverts to the latest gen.
        await svc.ClearAsync(user.Id, "monet", Ct);
        Assert.Equal(new ArtistDisplayResult("monet-new", false), await svc.GetDisplayAsync(user.Id, "monet", Ct));
    }

    [Fact]
    public async Task Set_rejects_an_image_not_in_the_users_history()
    {
        User user = await fixture.NewUserAsync("artist-reject");
        Assert.False(await NewService().SetAsync(user.Id, "monet", "not-mine", DateTime.UtcNow, Ct));
    }

    [Fact]
    public async Task ResolveMany_is_per_user_and_mixes_overrides_with_fallbacks()
    {
        User alice = await fixture.NewUserAsync("artist-many-a");
        User bob = await fixture.NewUserAsync("artist-many-b");
        await fixture.History.AddAsync(Gen(alice.Id, "a-monet", "monet", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)), Ct);
        await fixture.History.AddAsync(Gen(alice.Id, "a-vangogh", "van_gogh", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)), Ct);
        await fixture.History.AddAsync(Gen(bob.Id, "b-monet", "monet", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)), Ct);
        ArtistService svc = NewService();
        await svc.SetAsync(alice.Id, "van_gogh", "a-vangogh", DateTime.UtcNow, Ct);

        IReadOnlyDictionary<string, string> resolved = await svc.ResolveManyAsync(alice.Id, ["monet", "van_gogh", "unseen"], Ct);
        Assert.Equal("a-monet", resolved["monet"]);       // fallback to alice's gen
        Assert.Equal("a-vangogh", resolved["van_gogh"]);  // override
        Assert.False(resolved.ContainsKey("unseen"));      // no gen, no pick -> absent
        Assert.DoesNotContain("b-monet", resolved.Values); // never sees bob's image
    }

    private static HistoryEntry Gen(long userId, string imageId, string artist, DateTime createdAtUtc) => new()
    {
        UserId = userId,
        GatewayImageId = imageId,
        Prompt = "a prompt",
        ModelFriendly = "Test Model",
        ModelId = "test",
        Aspect = "square",
        CreatedAtUtc = createdAtUtc,
        Marks = [new Mark(artist, TokenKind.Artist)],
    };
}
