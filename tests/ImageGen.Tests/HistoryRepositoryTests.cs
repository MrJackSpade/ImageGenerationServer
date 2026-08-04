using ImageGen.Domain;
using ImageGen.Domain.Entities;
using ImageGen.Domain.Repositories;

namespace ImageGen.Tests;

[Collection("db")]
public sealed class HistoryRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Add_then_get_roundtrips_with_marks()
    {
        var user = await fixture.NewUserAsync("hist-roundtrip");
        var entry = Entry(user.Id, "img-1", marks:
        [
            new Mark("van_gogh", TokenKind.Artist),
            new Mark("oil_painting", TokenKind.Tag),
        ]);

        var added = await fixture.History.AddAsync(entry, Ct);
        Assert.True(added);

        var fetched = await fixture.History.GetByGatewayImageIdAsync(user.Id, "img-1", Ct);
        Assert.NotNull(fetched);
        Assert.Equal("a prompt", fetched.Prompt);
        Assert.Equal(2, fetched.Marks.Count);
        Assert.Contains(fetched.Marks, m => m is { Token: "van_gogh", Kind: TokenKind.Artist });
    }

    [Fact]
    public async Task The_raw_prompt_survives_the_round_trip_verbatim()
    {
        // The whole point of the column: what comes back out is byte-for-byte what went in — markers, underscores and
        // casing intact — so copy/Reload/Edit can hand it straight back to a prompt box instead of guessing it back
        // from the finalized text. It is encrypted at rest like Prompt, so this also pins the cipher round-trip.
        var user = await fixture.NewUserAsync("hist-rawprompt");
        var raw = "#long_hair, @Greg_Rutkowski, score_9, a plain phrase";

        await fixture.History.AddAsync(
            Entry(user.Id, "img-raw", marks: [new Mark("long_hair", TokenKind.Tag)], rawPrompt: raw), Ct);

        var fetched = await fixture.History.GetByGatewayImageIdAsync(user.Id, "img-raw", Ct);
        Assert.NotNull(fetched);
        Assert.Equal(raw, fetched.RawPrompt);
    }

    [Fact]
    public async Task The_raw_negative_survives_the_round_trip_and_null_stays_null()
    {
        // The negative is typed in the same marker dialect and shaped the picture, so Reload and the edit boxes need it
        // back verbatim. NULL ("no negative was submitted") must NOT come back as "": null leaves the model's built-in
        // default negative standing alone, and an empty string is a different render.
        var user = await fixture.NewUserAsync("hist-rawneg");
        var negative = "#bad_anatomy, @some_artist, blurry";

        await fixture.History.AddAsync(Entry(user.Id, "img-neg", rawNegative: negative), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "img-noneg"), Ct);

        var withNegative = await fixture.History.GetByGatewayImageIdAsync(user.Id, "img-neg", Ct);
        var without = await fixture.History.GetByGatewayImageIdAsync(user.Id, "img-noneg", Ct);
        Assert.NotNull(withNegative);
        Assert.NotNull(without);

        Assert.Equal(negative, withNegative.RawNegativePrompt);
        Assert.Null(without.RawNegativePrompt);
    }

    [Fact]
    public async Task A_row_written_without_a_raw_prompt_reads_back_null()
    {
        // Rows predating the column (and anything the backfill has not reached) must read as null, not "" — the caller
        // can then tell "never captured" apart from "captured, and it was empty".
        var user = await fixture.NewUserAsync("hist-rawnull");
        await fixture.History.AddAsync(Entry(user.Id, "img-norow"), Ct);

        var fetched = await fixture.History.GetByGatewayImageIdAsync(user.Id, "img-norow", Ct);
        Assert.NotNull(fetched);
        Assert.Null(fetched.RawPrompt);
    }

    [Fact]
    public async Task Add_is_deduped_by_user_and_image_id()
    {
        var user = await fixture.NewUserAsync("hist-dedupe");

        Assert.True(await fixture.History.AddAsync(Entry(user.Id, "dup"), Ct));
        Assert.False(await fixture.History.AddAsync(Entry(user.Id, "dup"), Ct));

        var page = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40), Ct);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task History_is_isolated_per_user()
    {
        var alice = await fixture.NewUserAsync("hist-alice");
        var bob = await fixture.NewUserAsync("hist-bob");
        await fixture.History.AddAsync(Entry(alice.Id, "shared-id"), Ct);

        var bobPage = await fixture.History.GetPageAsync(new HistoryQuery(bob.Id, 1, 40), Ct);
        Assert.Equal(0, bobPage.Total);

        // The same gateway image id is allowed under a different user (uniqueness is per-user).
        Assert.True(await fixture.History.AddAsync(Entry(bob.Id, "shared-id"), Ct));
    }

    [Fact]
    public async Task Get_page_filters_by_artist_mark()
    {
        var user = await fixture.NewUserAsync("hist-filter");
        await fixture.History.AddAsync(Entry(user.Id, "with-artist", marks: [new Mark("monet", TokenKind.Artist)]), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "no-artist"), Ct);

        var filtered = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40, Artist: "monet"), Ct);
        Assert.Equal(1, filtered.Total);
        Assert.Equal("with-artist", filtered.Items[0].GatewayImageId);
    }

    /// <summary>
    /// The prompt as TYPED is stored separately from the one that rendered, because it is the only record of the
    /// intent: the composer resolves [a|b] to the option it rolled before submitting, and that is one-directional.
    /// Null must stay null too — an image made before this was recorded has no original, and reporting the resolved
    /// prompt as one would hand back a string the user never typed.
    /// </summary>
    [Fact]
    public async Task The_original_prompt_is_stored_apart_from_the_one_that_rendered()
    {
        var user = await fixture.NewUserAsync("hist-original");
        await fixture.History.AddAsync(Entry(user.Id, "img-original",
            prompt: "1girl, blue hair", rawPrompt: "1girl, #blue_hair", original: "1girl, #[blue|red]_hair"), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "img-no-original", rawPrompt: "1girl"), Ct);

        var typed = await fixture.History.GetByGatewayImageIdAsync(user.Id, "img-original", Ct);
        Assert.NotNull(typed);
        Assert.Equal("1girl, #[blue|red]_hair", typed.OriginalPrompt);
        Assert.Equal("1girl, #blue_hair", typed.RawPrompt);   // what was submitted, already resolved
        Assert.Equal("1girl, blue hair", typed.Prompt);       // what the model rendered

        var none = await fixture.History.GetByGatewayImageIdAsync(user.Id, "img-no-original", Ct);
        Assert.NotNull(none);
        Assert.Null(none.OriginalPrompt);
    }

    /// <summary>
    /// An artist page shows what THAT artist's style looks like, so an image made with two or more artists belongs
    /// to no individual artist page — it would otherwise appear on every one of them, as evidence of none. Total
    /// has to follow, since the hero's "N generations" count comes from this same query.
    /// </summary>
    [Fact]
    public async Task Get_page_excludes_an_image_made_with_a_second_artist()
    {
        var user = await fixture.NewUserAsync("hist-multi-artist");
        await fixture.History.AddAsync(Entry(user.Id, "monet-only", marks: [new Mark("monet", TokenKind.Artist)]), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "blended", marks:
        [
            new Mark("monet", TokenKind.Artist),
            new Mark("picasso", TokenKind.Artist),
        ]), Ct);

        var monet = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40, Artist: "monet"), Ct);
        var picasso = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40, Artist: "picasso"), Ct);

        Assert.Equal(1, monet.Total);
        Assert.Equal("monet-only", monet.Items[0].GatewayImageId);
        Assert.Equal(0, picasso.Total);   // the blend is the ONLY picasso image, and it belongs to neither page
    }

    /// <summary>Ordinary TAG marks are not artists — they must not make a single-artist image look blended.</summary>
    [Fact]
    public async Task Get_page_keeps_a_single_artist_image_that_also_carries_tags()
    {
        var user = await fixture.NewUserAsync("hist-artist-with-tags");
        await fixture.History.AddAsync(Entry(user.Id, "monet-snow", marks:
        [
            new Mark("monet", TokenKind.Artist),
            new Mark("snow", TokenKind.Tag),
            new Mark("oil_painting", TokenKind.Tag),
        ]), Ct);

        var page = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40, Artist: "monet"), Ct);

        Assert.Equal(1, page.Total);
        Assert.Equal("monet-snow", page.Items[0].GatewayImageId);
    }

    /// <summary>
    /// The hero image and the bookmarks artist cards resolve through here, so it has to apply the same single-artist
    /// rule as the grid — otherwise @monet's card could be a picture that's half @picasso while @monet's own grid
    /// excludes it.
    /// </summary>
    [Fact]
    public async Task Latest_per_artist_skips_a_blended_image_for_the_newest_single_artist_one()
    {
        var user = await fixture.NewUserAsync("hist-latest-multi-artist");
        await fixture.History.AddAsync(Entry(user.Id, "monet-old", marks: [new Mark("monet", TokenKind.Artist)],
            created: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "blended-new", marks:
        [
            new Mark("monet", TokenKind.Artist),
            new Mark("picasso", TokenKind.Artist),
        ], created: new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc)), Ct);

        var latest = await fixture.History.GetLatestImageIdsForArtistsAsync(user.Id, ["monet", "picasso"], Ct);

        Assert.Equal("monet-old", latest["monet"]);   // newest is the blend; it represents neither artist
        Assert.False(latest.ContainsKey("picasso"));
    }

    /// <summary>
    /// The tag display-image fallback, and the one way it differs from the artist query: it is ADDITIVE. An image
    /// carrying two tags is the latest generation for BOTH of them — dropping the single-token rule is the whole
    /// point, since a picture legitimately wears many tags at once. The same case, run as artists, would exclude
    /// the blend from both (see <see cref="Latest_per_artist_skips_a_blended_image_for_the_newest_single_artist_one"/>).
    /// </summary>
    [Fact]
    public async Task Latest_per_tag_claims_an_image_that_also_carries_other_tags()
    {
        var user = await fixture.NewUserAsync("hist-latest-tags");
        await fixture.History.AddAsync(Entry(user.Id, "snow-only", marks: [new Mark("snow", TokenKind.Tag)],
            created: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "snowy-forest", marks:
        [
            new Mark("snow", TokenKind.Tag),
            new Mark("forest", TokenKind.Tag),
        ], created: new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc)), Ct);

        var latest = await fixture.History.GetLatestImageIdsForTagsAsync(user.Id, ["snow", "forest"], Ct);

        Assert.Equal("snowy-forest", latest["snow"]);     // newest snow image, even though it also carries "forest"
        Assert.Equal("snowy-forest", latest["forest"]);   // and that same image is forest's latest
    }

    /// <summary>An artist mark on the image is a different Kind, so it neither blocks nor supplies the tag fallback.</summary>
    [Fact]
    public async Task Latest_per_tag_ignores_artist_marks_on_the_image()
    {
        var user = await fixture.NewUserAsync("hist-latest-tag-artist");
        await fixture.History.AddAsync(Entry(user.Id, "monet-snow", marks:
        [
            new Mark("monet", TokenKind.Artist),
            new Mark("snow", TokenKind.Tag),
        ]), Ct);

        var latest = await fixture.History.GetLatestImageIdsForTagsAsync(user.Id, ["snow", "monet"], Ct);

        Assert.Equal("monet-snow", latest["snow"]);   // the artist mark does not disqualify it
        Assert.False(latest.ContainsKey("monet"));    // and an artist token is not a tag
    }

    /// <summary>
    /// The search box, end to end over the ENCRYPTED prompt column: no SQL predicate can read it, so the repository
    /// has to decrypt and match in memory. Every term must appear, and Total must count the matches (not the rows) or
    /// the page's infinite scroll stops at the wrong place.
    /// </summary>
    [Fact]
    public async Task Get_page_search_keeps_only_prompts_containing_every_term()
    {
        var user = await fixture.NewUserAsync("hist-search");
        await fixture.History.AddAsync(Entry(user.Id, "both", prompt: "hatsune miku, standing in snow"), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "one", prompt: "hatsune miku, on a beach"), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "neither", prompt: "a red car"), Ct);

        var hits = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40, Search: "MIKU snow"), Ct);

        Assert.Equal(1, hits.Total);
        Assert.Equal("both", hits.Items[0].GatewayImageId);
    }

    /// <summary>A search still pages: the skip/take applies to the MATCHES, in the same newest-first order.</summary>
    [Fact]
    public async Task Get_page_search_pages_over_the_matches()
    {
        var user = await fixture.NewUserAsync("hist-search-paging");
        for (var i = 0; i < 3; i++)
            await fixture.History.AddAsync(
                Entry(user.Id, $"m{i}", prompt: "a cat", created: new DateTime(2026, 1, 1, 12, i, 0, DateTimeKind.Utc)), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "other", prompt: "a dog"), Ct);

        var first = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 2, Search: "cat"), Ct);
        var second = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 2, 2, Search: "cat"), Ct);

        Assert.Equal(3, first.Total);
        Assert.Equal(["m2", "m1"], first.Items.Select(e => e.GatewayImageId));   // newest first
        Assert.Equal(["m0"], second.Items.Select(e => e.GatewayImageId));
    }

    /// <summary>Search composes with the mark filters instead of replacing them.</summary>
    [Fact]
    public async Task Get_page_search_combines_with_the_artist_filter()
    {
        var user = await fixture.NewUserAsync("hist-search-artist");
        await fixture.History.AddAsync(
            Entry(user.Id, "monet-cat", prompt: "a cat", marks: [new Mark("monet", TokenKind.Artist)]), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "plain-cat", prompt: "a cat"), Ct);
        await fixture.History.AddAsync(
            Entry(user.Id, "monet-dog", prompt: "a dog", marks: [new Mark("monet", TokenKind.Artist)]), Ct);

        var page = await fixture.History.GetPageAsync(
            new HistoryQuery(user.Id, 1, 40, Artist: "monet", Search: "cat"), Ct);

        Assert.Equal(1, page.Total);
        Assert.Equal("monet-cat", page.Items[0].GatewayImageId);
        Assert.Single(page.Items[0].Marks);   // the page's marks still load for the matched rows
    }

    /// <summary>The workflow filter, and that it composes with the search rather than replacing it.</summary>
    [Fact]
    public async Task Get_page_filters_by_workflow_and_combines_with_search()
    {
        var user = await fixture.NewUserAsync("hist-workflow");
        await fixture.History.AddAsync(Entry(user.Id, "a-cat", prompt: "a cat", modelId: "wf-a"), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "a-dog", prompt: "a dog", modelId: "wf-a"), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "b-cat", prompt: "a cat", modelId: "wf-b"), Ct);

        var byWorkflow = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40, Model: "wf-a"), Ct);
        var both = await fixture.History.GetPageAsync(
            new HistoryQuery(user.Id, 1, 40, Model: "wf-a", Search: "cat"), Ct);

        Assert.Equal(2, byWorkflow.Total);
        Assert.Equal(1, both.Total);
        Assert.Equal("a-cat", both.Items[0].GatewayImageId);
    }

    /// <summary>
    /// The filter's options: only workflows the user has actually used, most-used first, counted. The display name
    /// comes from their most recent generation with it, so a renamed workflow lists once under its current name.
    /// </summary>
    [Fact]
    public async Task Used_workflows_are_counted_most_used_first_under_their_latest_name()
    {
        var user = await fixture.NewUserAsync("hist-used-workflows");
        var noon = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await fixture.History.AddAsync(
            Entry(user.Id, "old-name", modelId: "wf-a", modelFriendly: "Old Name", created: noon), Ct);
        await fixture.History.AddAsync(
            Entry(user.Id, "new-name", modelId: "wf-a", modelFriendly: "New Name", created: noon.AddMinutes(1)), Ct);
        await fixture.History.AddAsync(
            Entry(user.Id, "other", modelId: "wf-b", modelFriendly: "Other", created: noon.AddMinutes(2)), Ct);

        var used = await fixture.History.GetUsedWorkflowsAsync(user.Id, Ct);

        Assert.Equal(2, used.Count);
        Assert.Equal(new HistoryWorkflowUse("wf-a", "New Name", 2), used[0]);   // most used, latest name
        Assert.Equal(new HistoryWorkflowUse("wf-b", "Other", 1), used[1]);
    }

    /// <summary>The options are per user — one account's workflows never appear in another's filter.</summary>
    [Fact]
    public async Task Used_workflows_are_scoped_to_the_user()
    {
        var alice = await fixture.NewUserAsync("hist-used-alice");
        var bob = await fixture.NewUserAsync("hist-used-bob");
        await fixture.History.AddAsync(Entry(alice.Id, "a", modelId: "wf-alice"), Ct);

        Assert.Empty(await fixture.History.GetUsedWorkflowsAsync(bob.Id, Ct));
        Assert.Single(await fixture.History.GetUsedWorkflowsAsync(alice.Id, Ct));
    }

    /// <summary>The raw (marker-form) prompt is searched too, so an image is findable by what was actually typed.</summary>
    [Fact]
    public async Task Get_page_search_reaches_the_raw_prompt()
    {
        var user = await fixture.NewUserAsync("hist-search-raw");
        await fixture.History.AddAsync(
            Entry(user.Id, "raw-hit", prompt: "1girl, smile", rawPrompt: "#long_hair, 1girl, smile"), Ct);

        var page = await fixture.History.GetPageAsync(new HistoryQuery(user.Id, 1, 40, Search: "long_hair"), Ct);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Delete_removes_entry_and_marks()
    {
        var user = await fixture.NewUserAsync("hist-delete");
        await fixture.History.AddAsync(Entry(user.Id, "del", marks: [new Mark("x", TokenKind.Tag)]), Ct);

        Assert.True(await fixture.ImageDeletions.DeleteEverywhereAsync(user.Id, "del", Ct));
        Assert.False(await fixture.ImageDeletions.DeleteEverywhereAsync(user.Id, "del", Ct));
        Assert.Null(await fixture.History.GetByGatewayImageIdAsync(user.Id, "del", Ct));
    }

    /// <summary>Deleting an image must take the bytes and every reference with it — the whole point of the cascade.
    /// Leaving any of these behind would strand unreachable blobs and dangling bookmarks in the database.</summary>
    [Fact]
    public async Task Delete_removes_the_blob_and_everything_referencing_it()
    {
        var user = await fixture.NewUserAsync("hist-delete-cascade");
        var imageId = await fixture.Blobs.AddAsync(
            new NewImageBlob([1, 2, 3, 4], "image/png", 64, 64, ImageBlobKind.Generated), Ct);

        await fixture.History.AddAsync(Entry(user.Id, imageId, marks: [new Mark("monet", TokenKind.Artist)]), Ct);
        await fixture.Bookmarks.AddImageAsync(new ImageBookmark
        {
            UserId = user.Id,
            GatewayImageId = imageId,
            Prompt = "a prompt",
            ModelFriendly = "Model",
            ModelId = "model",
            Aspect = "1:1",
            OriginalCreatedAtUtc = DateTime.UtcNow,
            SavedAtUtc = DateTime.UtcNow,
        }, Ct);
        await fixture.ArtistDisplays.SetAsync(new ArtistDisplay
        {
            UserId = user.Id,
            ArtistName = "monet",
            GatewayImageId = imageId,
            SetAtUtc = DateTime.UtcNow,
        }, Ct);

        Assert.True(await fixture.ImageDeletions.DeleteEverywhereAsync(user.Id, imageId, Ct));

        Assert.Null(await fixture.History.GetByGatewayImageIdAsync(user.Id, imageId, Ct));
        Assert.Null(await fixture.Blobs.GetAsync(imageId, Ct));
        Assert.False(await fixture.Bookmarks.IsImageBookmarkedAsync(user.Id, imageId, Ct));
        Assert.Null(await fixture.ArtistDisplays.GetAsync(user.Id, "monet", Ct));
    }

    /// <summary>One user's delete must not reach into another user's rows, even for the same image id.</summary>
    [Fact]
    public async Task Delete_is_scoped_to_the_owner()
    {
        var alice = await fixture.NewUserAsync("hist-delete-alice");
        var bob = await fixture.NewUserAsync("hist-delete-bob");
        await fixture.History.AddAsync(Entry(alice.Id, "shared-delete"), Ct);
        await fixture.History.AddAsync(Entry(bob.Id, "shared-delete"), Ct);

        Assert.True(await fixture.ImageDeletions.DeleteEverywhereAsync(alice.Id, "shared-delete", Ct));

        Assert.Null(await fixture.History.GetByGatewayImageIdAsync(alice.Id, "shared-delete", Ct));
        Assert.NotNull(await fixture.History.GetByGatewayImageIdAsync(bob.Id, "shared-delete", Ct));
    }

    /// <summary>
    /// The prev/next arrows. Ordering is <c>(CreatedAtUtc, Id)</c> DESC-is-older, so "newer" walks forward in time.
    /// </summary>
    [Fact]
    public async Task Neighbors_walk_the_users_history_in_both_directions()
    {
        var user = await fixture.NewUserAsync("hist-neighbors");
        var t0 = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        await fixture.History.AddAsync(Entry(user.Id, "n-old", created: t0), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "n-mid", created: t0.AddMinutes(1)), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "n-new", created: t0.AddMinutes(2)), Ct);

        var mid = await fixture.History.GetNeighborsAsync(user.Id, "n-mid", Ct);
        Assert.Equal("n-new", mid.NewerId);
        Assert.Equal("n-old", mid.OlderId);

        // The ends report null on the side that runs out, not the nearest row on the wrong side.
        var newest = await fixture.History.GetNeighborsAsync(user.Id, "n-new", Ct);
        Assert.Null(newest.NewerId);
        Assert.Equal("n-mid", newest.OlderId);

        var oldest = await fixture.History.GetNeighborsAsync(user.Id, "n-old", Ct);
        Assert.Equal("n-mid", oldest.NewerId);
        Assert.Null(oldest.OlderId);
    }

    /// <summary>
    /// Entries sharing a timestamp — a batch writes several in the same instant — must still order by Id, or the
    /// arrows can loop between two images forever. This is why the comparison is on the (CreatedAtUtc, Id) pair.
    /// </summary>
    [Fact]
    public async Task Neighbors_break_a_timestamp_tie_on_id()
    {
        var user = await fixture.NewUserAsync("hist-neighbors-tie");
        var same = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        await fixture.History.AddAsync(Entry(user.Id, "tie-a", created: same), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "tie-b", created: same), Ct);
        await fixture.History.AddAsync(Entry(user.Id, "tie-c", created: same), Ct);

        var b = await fixture.History.GetNeighborsAsync(user.Id, "tie-b", Ct);
        Assert.Equal("tie-c", b.NewerId);
        Assert.Equal("tie-a", b.OlderId);
    }

    /// <summary>Another user's entries are not neighbours, and an id nobody owns has none at all.</summary>
    [Fact]
    public async Task Neighbors_are_scoped_to_the_user_and_absent_for_an_unknown_image()
    {
        var alice = await fixture.NewUserAsync("hist-neighbors-alice");
        var bob = await fixture.NewUserAsync("hist-neighbors-bob");
        var t0 = new DateTime(2026, 3, 3, 8, 0, 0, DateTimeKind.Utc);
        await fixture.History.AddAsync(Entry(alice.Id, "a-only", created: t0), Ct);
        await fixture.History.AddAsync(Entry(bob.Id, "b-newer", created: t0.AddMinutes(5)), Ct);

        var alone = await fixture.History.GetNeighborsAsync(alice.Id, "a-only", Ct);
        Assert.Null(alone.NewerId);
        Assert.Null(alone.OlderId);

        var missing = await fixture.History.GetNeighborsAsync(alice.Id, "no-such-image", Ct);
        Assert.Null(missing.NewerId);
        Assert.Null(missing.OlderId);
    }

    private static HistoryEntry Entry(
        long userId, string imageId, IReadOnlyList<Mark>? marks = null, string? rawPrompt = null,
        string? rawNegative = null, string prompt = "a prompt", DateTime? created = null,
        string modelId = "test", string modelFriendly = "Test Model", string? original = null) => new()
    {
        UserId = userId,
        GatewayImageId = imageId,
        Prompt = prompt,
        RawPrompt = rawPrompt,
        RawNegativePrompt = rawNegative,
        OriginalPrompt = original,
        ModelFriendly = modelFriendly,
        ModelId = modelId,
        Aspect = "square",
        CreatedAtUtc = created ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        Marks = marks ?? [],
    };
}
