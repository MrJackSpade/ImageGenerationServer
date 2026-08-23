using ImageGen.Application.Services;
using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

[Collection("db")]
public sealed class HideWorkflowTipsPersistenceTests(TestDatabaseFixture fixture)
{
    [Fact]
    public async Task Toggle_starts_off_and_round_trips_per_user()
    {
        UserService service = new(fixture.Users, TimeProvider.System);
        User? user = await service.RegisterAsync("hide_tips_user", "password1", "", CancellationToken.None);
        Assert.NotNull(user);
        Assert.False(user.HideWorkflowTips);

        await service.SetHideWorkflowTipsAsync(user.Id, true, CancellationToken.None);
        User? reloaded = await service.GetByIdAsync(user.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.HideWorkflowTips);

        await service.SetHideWorkflowTipsAsync(user.Id, false, CancellationToken.None);
        reloaded = await service.GetByIdAsync(user.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.HideWorkflowTips);
    }
}
