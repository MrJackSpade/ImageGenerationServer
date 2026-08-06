using ImageGen.Domain;
using ImageGen.Domain.Entities;

namespace ImageGen.Tests;

[Collection("db")]
public sealed class BookmarkRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Token_add_is_deduped_and_split_by_kind()
    {
        User user = await fixture.NewUserAsync("bm-tokens");

        Assert.True(await fixture.Bookmarks.AddTokenAsync(Token(user.Id, "monet", TokenKind.Artist), Ct));
        Assert.False(await fixture.Bookmarks.AddTokenAsync(Token(user.Id, "monet", TokenKind.Artist), Ct));
        Assert.True(await fixture.Bookmarks.AddTokenAsync(Token(user.Id, "monet", TokenKind.Tag), Ct)); // same name, other kind

        IReadOnlyList<TokenBookmark> tokens = await fixture.Bookmarks.GetTokensAsync(user.Id, Ct);
        Assert.Equal(2, tokens.Count);
        _ = Assert.Single(tokens, t => t.Kind == TokenKind.Artist);
        _ = Assert.Single(tokens, t => t.Kind == TokenKind.Tag);
    }

    [Fact]
    public async Task Token_remove_reports_whether_it_existed()
    {
        User user = await fixture.NewUserAsync("bm-token-remove");
        _ = await fixture.Bookmarks.AddTokenAsync(Token(user.Id, "cats", TokenKind.Tag), Ct);

        Assert.True(await fixture.Bookmarks.RemoveTokenAsync(user.Id, "cats", TokenKind.Tag, Ct));
        Assert.False(await fixture.Bookmarks.RemoveTokenAsync(user.Id, "cats", TokenKind.Tag, Ct));
    }

    [Fact]
    public async Task Image_bookmark_roundtrips_with_marks_and_dedupes()
    {
        User user = await fixture.NewUserAsync("bm-image");
        ImageBookmark bookmark = Image(user.Id, "img-9", marks: [new Mark("rembrandt", TokenKind.Artist)]);

        Assert.True(await fixture.Bookmarks.AddImageAsync(bookmark, Ct));
        Assert.False(await fixture.Bookmarks.AddImageAsync(bookmark, Ct));

        IReadOnlyList<ImageBookmark> images = await fixture.Bookmarks.GetImagesAsync(user.Id, Ct);
        _ = Assert.Single(images);
        Assert.Equal("img-9", images[0].GatewayImageId);
        Assert.Contains(images[0].Marks, m => m is { Token: "rembrandt", Kind: TokenKind.Artist });
    }

    [Fact]
    public async Task Bookmarks_are_isolated_per_user()
    {
        User alice = await fixture.NewUserAsync("bm-alice");
        User bob = await fixture.NewUserAsync("bm-bob");
        _ = await fixture.Bookmarks.AddTokenAsync(Token(alice.Id, "secret", TokenKind.Tag), Ct);
        _ = await fixture.Bookmarks.AddImageAsync(Image(alice.Id, "secret-img"), Ct);

        Assert.Empty(await fixture.Bookmarks.GetTokensAsync(bob.Id, Ct));
        Assert.Empty(await fixture.Bookmarks.GetImagesAsync(bob.Id, Ct));
    }

    [Fact]
    public async Task Setting_token_categories_creates_the_bookmark_and_replaces_the_set()
    {
        User user = await fixture.NewUserAsync("bm-token-cats");

        // Long-pressing an un-starred chip and filing it should create the bookmark itself.
        await fixture.Bookmarks.SetTokenCategoriesAsync(
            Token(user.Id, "vangogh", TokenKind.Artist), ["Post-Impressionism", "Favorites"], Ct);

        IReadOnlyList<TokenBookmark> tokens = await fixture.Bookmarks.GetTokensAsync(user.Id, Ct);
        _ = Assert.Single(tokens);
        Assert.Equal(
            new[] { "Favorites", "Post-Impressionism" },
            tokens[0].Categories.OrderBy(c => c).ToArray());

        // A second set replaces (not merges) the whole set.
        await fixture.Bookmarks.SetTokenCategoriesAsync(
            Token(user.Id, "vangogh", TokenKind.Artist), ["Favorites"], Ct);
        IReadOnlyList<string> after = await fixture.Bookmarks.GetTokenCategoriesAsync(user.Id, "vangogh", TokenKind.Artist, Ct);
        Assert.Equal(["Favorites"], after);
        _ = Assert.Single(await fixture.Bookmarks.GetTokensAsync(user.Id, Ct)); // still one bookmark
    }

    [Fact]
    public async Task Setting_image_categories_creates_the_bookmark_and_lists_distinct_names()
    {
        User user = await fixture.NewUserAsync("bm-image-cats");

        await fixture.Bookmarks.SetImageCategoriesAsync(Image(user.Id, "img-cat"), ["Landscapes", "Refs"], Ct);
        await fixture.Bookmarks.SetTokenCategoriesAsync(
            Token(user.Id, "field", TokenKind.Tag), ["Refs"], Ct); // shared category across kinds

        IReadOnlyList<ImageBookmark> images = await fixture.Bookmarks.GetImagesAsync(user.Id, Ct);
        _ = Assert.Single(images);
        Assert.Equal(new[] { "Landscapes", "Refs" }, images[0].Categories.OrderBy(c => c).ToArray());

        // The distinct list spans both bookmark kinds, deduped and name-sorted.
        IReadOnlyList<string> all = await fixture.Bookmarks.GetAllCategoriesAsync(user.Id, Ct);
        Assert.Equal(new[] { "Landscapes", "Refs" }, all);
    }

    [Fact]
    public async Task Removing_a_bookmark_clears_its_categories_from_the_distinct_list()
    {
        User user = await fixture.NewUserAsync("bm-cat-cascade");
        await fixture.Bookmarks.SetTokenCategoriesAsync(Token(user.Id, "temp", TokenKind.Tag), ["Scratch"], Ct);

        Assert.Equal(["Scratch"], await fixture.Bookmarks.GetAllCategoriesAsync(user.Id, Ct));
        Assert.True(await fixture.Bookmarks.RemoveTokenAsync(user.Id, "temp", TokenKind.Tag, Ct));
        Assert.Empty(await fixture.Bookmarks.GetAllCategoriesAsync(user.Id, Ct)); // membership cascaded away
    }

    private static TokenBookmark Token(long userId, string name, TokenKind kind) => new()
    {
        UserId = userId,
        Name = name,
        Kind = kind,
        SavedAtUtc = new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc),
    };

    private static ImageBookmark Image(long userId, string imageId, IReadOnlyList<Mark>? marks = null) => new()
    {
        UserId = userId,
        GatewayImageId = imageId,
        Prompt = "saved prompt",
        ModelFriendly = "Test Model",
        ModelId = "test",
        Aspect = "portrait",
        OriginalCreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        SavedAtUtc = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
        Marks = marks ?? [],
    };
}
