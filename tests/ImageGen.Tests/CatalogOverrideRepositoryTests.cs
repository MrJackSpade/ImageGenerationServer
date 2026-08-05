using ImageGen.Domain.Repositories;

namespace ImageGen.Tests;

/// <summary>
/// The install-wide catalogue overrides: which file fills a slot on this machine, and what per-configuration
/// settings differ here.
///
/// <para>These run on whichever engine the suite is pointed at, so the same assertions cover SQL Server and
/// SQLite — which matters because the two tables use a delete-then-insert upsert rather than either engine's
/// native one, precisely so there is a single spelling to test.</para>
/// </summary>
[Collection("db")]
public sealed class CatalogOverrideRepositoryTests(TestDatabaseFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private ICatalogOverrideRepository Repo => fixture.CatalogOverrides;

    [Fact]
    public async Task A_binding_round_trips()
    {
        const string machine = "bind-roundtrip";
        await Repo.SetBindingAsync(machine, "pony-v6", "myPony_v6.safetensors", isAuto: false, Ct);

        IReadOnlyDictionary<string, ModelBinding> all = await Repo.BindingsAsync(machine, Ct);
        Assert.True(all.TryGetValue("pony-v6", out ModelBinding? b));
        Assert.NotNull(b);
        Assert.Equal("myPony_v6.safetensors", b.FileName);
        Assert.False(b.IsAuto);
    }

    /// <summary>
    /// Bindings describe a machine's disk, so two machines sharing one SQL Server database must not see each
    /// other's. This is the same reason GenTiming and Job carry MachineName.
    /// </summary>
    [Fact]
    public async Task Bindings_are_isolated_per_machine()
    {
        await Repo.SetBindingAsync("box-a", "shared-slot", "a.safetensors", false, Ct);
        await Repo.SetBindingAsync("box-b", "shared-slot", "b.safetensors", false, Ct);

        Assert.Equal("a.safetensors", (await Repo.BindingsAsync("box-a", Ct))["shared-slot"].FileName);
        Assert.Equal("b.safetensors", (await Repo.BindingsAsync("box-b", Ct))["shared-slot"].FileName);
    }

    [Fact]
    public async Task Setting_a_binding_twice_replaces_rather_than_duplicating()
    {
        const string machine = "bind-replace";
        await Repo.SetBindingAsync(machine, "slot", "first.safetensors", false, Ct);
        await Repo.SetBindingAsync(machine, "slot", "second.safetensors", false, Ct);

        IReadOnlyDictionary<string, ModelBinding> all = await Repo.BindingsAsync(machine, Ct);
        Assert.Single(all);
        Assert.Equal("second.safetensors", all["slot"].FileName);
    }

    /// <summary>Clearing is how a user rejects a wrong automatic guess, so it has to actually remove the row.</summary>
    [Fact]
    public async Task A_blank_filename_clears_the_binding()
    {
        const string machine = "bind-clear";
        await Repo.SetBindingAsync(machine, "slot", "something.safetensors", false, Ct);
        await Repo.SetBindingAsync(machine, "slot", null, false, Ct);

        Assert.Empty(await Repo.BindingsAsync(machine, Ct));
    }

    /// <summary>
    /// The load-bearing one. Auto-mapping runs on every catalogue load; if it could overwrite a binding the user
    /// chose, their correction would survive exactly until the next restart.
    /// </summary>
    [Fact]
    public async Task Auto_binding_never_overwrites_a_hand_picked_one()
    {
        const string machine = "auto-vs-manual";
        await Repo.SetBindingAsync(machine, "taken", "the-one-I-picked.safetensors", isAuto: false, Ct);

        await Repo.AddAutoBindingsAsync(machine, new Dictionary<string, string>
        {
            ["taken"] = "what-the-pattern-guessed.safetensors",
            ["free"] = "guessed.safetensors",
        }, Ct);

        IReadOnlyDictionary<string, ModelBinding> all = await Repo.BindingsAsync(machine, Ct);
        Assert.Equal("the-one-I-picked.safetensors", all["taken"].FileName);
        Assert.False(all["taken"].IsAuto);

        // The unbound slot does get filled, and is marked as a guess so it can be re-evaluated later.
        Assert.Equal("guessed.safetensors", all["free"].FileName);
        Assert.True(all["free"].IsAuto);
    }

    [Fact]
    public async Task Auto_binding_does_not_overwrite_an_earlier_auto_binding_either()
    {
        const string machine = "auto-twice";
        await Repo.AddAutoBindingsAsync(machine, new Dictionary<string, string> { ["s"] = "first.safetensors" }, Ct);
        await Repo.AddAutoBindingsAsync(machine, new Dictionary<string, string> { ["s"] = "second.safetensors" }, Ct);

        Assert.Equal("first.safetensors", (await Repo.BindingsAsync(machine, Ct))["s"].FileName);
    }

    [Fact]
    public async Task Overrides_round_trip_keyed_by_config_and_setting()
    {
        const string machine = "ovr-roundtrip";
        await Repo.SetOverrideAsync(machine, "flux2-dev", "vram.min", "12000", Ct);
        await Repo.SetOverrideAsync(machine, "flux2-dev", "param.steps", "28", Ct);
        await Repo.SetOverrideAsync(machine, "anima", "visible.ui", "false", Ct);

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> all = await Repo.OverridesAsync(machine, Ct);
        Assert.Equal("12000", all["flux2-dev"]["vram.min"]);
        Assert.Equal("28", all["flux2-dev"]["param.steps"]);
        Assert.Equal("false", all["anima"]["visible.ui"]);
    }

    [Fact]
    public async Task Setting_an_override_twice_replaces_it()
    {
        const string machine = "ovr-replace";
        await Repo.SetOverrideAsync(machine, "cfg", "vram.min", "8000", Ct);
        await Repo.SetOverrideAsync(machine, "cfg", "vram.min", "4000", Ct);

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> all = await Repo.OverridesAsync(machine, Ct);
        Assert.Single(all["cfg"]);
        Assert.Equal("4000", all["cfg"]["vram.min"]);
    }

    /// <summary>
    /// Blank REMOVES rather than storing an empty string, so "put this back to the shipped default" is a distinct
    /// outcome from "set it to nothing" — otherwise there is no way to undo an override at all.
    /// </summary>
    [Fact]
    public async Task A_blank_value_removes_the_override_and_restores_the_shipped_default()
    {
        const string machine = "ovr-clear";
        await Repo.SetOverrideAsync(machine, "cfg", "vram.min", "8000", Ct);
        await Repo.SetOverrideAsync(machine, "cfg", "vram.min", null, Ct);

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> all = await Repo.OverridesAsync(machine, Ct);
        Assert.False(all.TryGetValue("cfg", out IReadOnlyDictionary<string, string>? settings) && settings.ContainsKey("vram.min"));
    }

    [Fact]
    public async Task An_unknown_machine_has_no_bindings_and_no_overrides()
    {
        Assert.Empty(await Repo.BindingsAsync("never-seen", Ct));
        Assert.Empty(await Repo.OverridesAsync("never-seen", Ct));
    }
}
