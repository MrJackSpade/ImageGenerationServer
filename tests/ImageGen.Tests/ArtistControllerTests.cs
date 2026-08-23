using System.Security.Claims;
using ImageGen.Application.Services;
using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Web.Controllers;
using ImageGen.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Tests;

[Collection("db")]
public sealed class ArtistControllerTests(TestDatabaseFixture fixture)
{
    [Fact]
    public async Task Encoded_slash_is_decoded_before_lookup_and_rendering()
    {
        User user = await fixture.NewUserAsync("artist-slash");
        _ = await fixture.History.AddAsync(new HistoryEntry
        {
            UserId = user.Id,
            GatewayImageId = "slash-artist-image",
            Prompt = "a prompt",
            ModelFriendly = "Test Model",
            ModelId = "test",
            Aspect = "square",
            CreatedAtUtc = DateTime.UtcNow,
            Marks = [new Mark("studio/artist", TokenKind.Artist)],
        }, CancellationToken.None);

        ArtistController controller = new(
            new ArtistService(fixture.ArtistDisplays, fixture.History),
            new ImageViewService(fixture.ImageViews, TimeProvider.System))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test"))
                }
            }
        };

        ViewResult result = Assert.IsType<ViewResult>(
            await controller.Index("studio%2Fartist", CancellationToken.None));
        ArtistViewModel model = Assert.IsType<ArtistViewModel>(result.Model);

        Assert.Equal("studio/artist", model.Name);
        Assert.Equal("slash-artist-image", model.DisplayImageId);
        Assert.Equal(1, model.Total);
    }
}
