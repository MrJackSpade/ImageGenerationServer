using ImageGen.Application.Services;
using ImageGen.Application.Tags;

namespace ImageGen.Tests;

/// <summary>The generation mask's rules: what an unset/stored value resolves to, and what a selection is allowed to be.</summary>
public sealed class GenerationTagTypesTests
{
    [Fact]
    public void Unset_resolves_to_the_default_artists_off()
    {
        Assert.Equal(GenerationTagTypes.Default, GenerationTagTypes.Resolve(null));
        Assert.Equal(GenerationTagTypes.Default, GenerationTagTypes.Resolve("   "));
        Assert.DoesNotContain("artist", GenerationTagTypes.Default);
    }

    [Fact]
    public void Empty_selection_is_a_real_choice_not_the_default()
    {
        Assert.True(GenerationTagTypes.TryNormalize(Array.Empty<string>(), out var none, out _));
        Assert.Empty(none);
        Assert.Empty(GenerationTagTypes.Resolve(GenerationTagTypes.Serialize(none)));   // "[]" != unset
    }

    [Fact]
    public void Selection_is_canonicalised_and_round_trips()
    {
        Assert.True(GenerationTagTypes.TryNormalize(new[] { "META", " artist ", "meta" }, out var types, out _));
        Assert.Equal(new[] { "artist", "meta" }, types);                                // deduped, in display order
        Assert.Equal(types, GenerationTagTypes.Resolve(GenerationTagTypes.Serialize(types)));
    }

    [Fact]
    public void Unknown_type_is_rejected_never_dropped()
    {
        Assert.False(GenerationTagTypes.TryNormalize(new[] { "character", "seiyuu" }, out _, out var error));
        Assert.Contains("seiyuu", error);
    }

    /// <summary>
    /// Every category the model can suppress is now switchable, `general` included — so the switch list
    /// covers the model's whole DROPPABLE set and the selection IS the wire list. If the model gains a category, it
    /// belongs in Selectable in the same change: `types=` names what stays ALLOWED, so one the app forgets to name is
    /// one the model silently switches off.
    /// </summary>
    [Fact]
    public void Every_suppressible_type_is_switchable_and_general_can_be_turned_off()
    {
        Assert.Equal(new[] { "general", "artist", "character", "copyright", "meta" }, GenerationTagTypes.Selectable);

        // General off is a real, selectable condition — a set like {traditional_media, colored_pencil}.
        Assert.True(GenerationTagTypes.TryNormalize(new[] { "meta" }, out var metaOnly, out _));
        Assert.Equal(new[] { "meta" }, metaOnly);
        Assert.Equal(metaOnly, GenerationTagTypes.Resolve(GenerationTagTypes.Serialize(metaOnly)));

        // The wire list is the selection verbatim: nothing is added behind the user's back, nothing is filtered.
        Assert.Equal(GenerationTagTypes.Default, GenerationTagTypes.ForWire(GenerationTagTypes.Default));
        Assert.Empty(GenerationTagTypes.ForWire(Array.Empty<string>()));
        // The unset default still allows general — artists are the only thing off until the user says otherwise.
        Assert.Contains("general", GenerationTagTypes.Default);
    }

    /// <summary>
    /// A v1 value (bare array) was written when `general` was not switchable and therefore always allowed. Read under
    /// today's rules it would mean "general OFF" — collapsing every prompt to a couple of meta tags for every user who
    /// had ever touched this setting. It is upgraded on read; the version tag is what makes that distinguishable from a
    /// user who deliberately turned general off today.
    /// </summary>
    [Fact]
    public void Legacy_stored_value_is_upgraded_not_read_as_general_off()
    {
        Assert.Equal(new[] { "general", "character", "copyright", "meta" },
                     GenerationTagTypes.Resolve("[\"character\",\"copyright\",\"meta\"]"));
        // v1's empty selection meant "every switchable type off", and general was not switchable — so it stays on.
        Assert.Equal(new[] { "general" }, GenerationTagTypes.Resolve("[]"));
        // ...whereas the same selection saved TODAY means general really is off, and survives the round trip.
        Assert.True(GenerationTagTypes.TryNormalize(new[] { "character", "copyright", "meta" }, out var today, out _));
        Assert.Equal(today, GenerationTagTypes.Resolve(GenerationTagTypes.Serialize(today)));
        Assert.DoesNotContain("general", GenerationTagTypes.Resolve(GenerationTagTypes.Serialize(today)));
    }

    [Fact]
    public void Corrupt_stored_value_throws_rather_than_silently_masking()
    {
        Assert.Throws<InvalidOperationException>(() => GenerationTagTypes.Resolve("not json"));
        Assert.Throws<InvalidOperationException>(() => GenerationTagTypes.Resolve("[\"seiyuu\"]"));
        Assert.Throws<InvalidOperationException>(() => GenerationTagTypes.Resolve("{\"v\":99,\"types\":[\"meta\"]}"));
        Assert.Throws<InvalidOperationException>(() => GenerationTagTypes.Resolve("42"));
    }
}

[Collection("db")]
public sealed class GenerationTagTypesPersistenceTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private UserService Service() => new(fixture.Users, TimeProvider.System);

    [Fact]
    public async Task Mask_persists_per_user_and_starts_unset()
    {
        var svc = Service();
        var user = await svc.RegisterAsync("mask_user", "password1", "", Ct);
        Assert.NotNull(user);
        Assert.Null(user.GenerationTagTypes);                                          // unset until it is set
        Assert.Equal(GenerationTagTypes.Default, GenerationTagTypes.Resolve(user.GenerationTagTypes));

        Assert.Null(await svc.SetGenerationTagTypesAsync(user.Id, new[] { "artist", "character" }, Ct));
        var reloaded = await svc.GetByIdAsync(user.Id, Ct);
        Assert.NotNull(reloaded);
        Assert.Equal(new[] { "artist", "character" }, GenerationTagTypes.Resolve(reloaded.GenerationTagTypes));

        // ...including the empty selection, which must survive as "none of them" rather than reading as unset.
        Assert.Null(await svc.SetGenerationTagTypesAsync(user.Id, Array.Empty<string>(), Ct));
        reloaded = await svc.GetByIdAsync(user.Id, Ct);
        Assert.NotNull(reloaded);
        Assert.Empty(GenerationTagTypes.Resolve(reloaded.GenerationTagTypes));
    }

    [Fact]
    public async Task Rejected_selection_writes_nothing()
    {
        var svc = Service();
        var user = await svc.RegisterAsync("mask_reject_user", "password1", "", Ct);
        Assert.NotNull(user);
        Assert.Null(await svc.SetGenerationTagTypesAsync(user.Id, new[] { "meta" }, Ct));

        var error = await svc.SetGenerationTagTypesAsync(user.Id, new[] { "character", "nonsense" }, Ct);
        Assert.NotNull(error);
        var reloaded = await svc.GetByIdAsync(user.Id, Ct);
        Assert.NotNull(reloaded);
        Assert.Equal(new[] { "meta" }, GenerationTagTypes.Resolve(reloaded.GenerationTagTypes));
    }
}
