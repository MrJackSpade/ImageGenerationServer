using ImageGen.Web.Configuration;
using Microsoft.Extensions.Configuration;

namespace ImageGen.Tests;

/// <summary>
/// This machine's configuration comes out of the database and behaves like any other configuration source — which
/// is the whole point of it: every existing <c>config["..."]</c> read keeps working, and a write is visible to the
/// next read without a restart.
///
/// <para>Engine-agnostic like the rest of the suite. Set <c>IMAGEGEN_TEST_SQLSERVER=1</c> to run it against SQL
/// Server; the assertions do not name a provider.</para>
/// </summary>
[Collection("db")]
public sealed class MachineSettingsTests(TestDatabaseFixture db)
{
    private readonly TestDatabaseFixture _db = db;

    private (IConfigurationRoot Config, MachineSettingsConfigurationSource Source) BuildConfig(string machine)
    {
        MachineSettingsConfigurationSource source = new(_db.MachineSettings, machine);
        IConfigurationRoot config = new ConfigurationBuilder().Add(source).Build();
        return (config, source);
    }

    [Fact]
    public async Task Stored_values_appear_as_configuration()
    {
        const string machine = "cfg-read";
        await _db.MachineSettings.SetAsync(machine, "ComfyUI:BaseUrl", "http://box:8188", CancellationToken.None);

        (IConfigurationRoot? config, MachineSettingsConfigurationSource _) = BuildConfig(machine);

        Assert.Equal("http://box:8188", config["ComfyUI:BaseUrl"]);
    }

    [Fact]
    public async Task A_write_is_visible_to_the_next_read_without_rebuilding()
    {
        const string machine = "cfg-live";
        (IConfigurationRoot? config, MachineSettingsConfigurationSource? source) = BuildConfig(machine);
        Assert.NotNull(source.Provider);
        Assert.Null(config["ComfyUI:BaseUrl"]);

        await source.Provider.WriteAsync("ComfyUI:BaseUrl", "http://moved:9000", CancellationToken.None);

        // The same IConfiguration instance — nothing was rebuilt. This is what makes the renderer's address
        // changeable while the app is running.
        Assert.Equal("http://moved:9000", config["ComfyUI:BaseUrl"]);
    }

    [Fact]
    public async Task A_write_fires_the_change_token()
    {
        const string machine = "cfg-token";
        (IConfigurationRoot? config, MachineSettingsConfigurationSource? source) = BuildConfig(machine);
        Assert.NotNull(source.Provider);
        bool fired = false;
        _ = Microsoft.Extensions.Primitives.ChangeToken.OnChange(config.GetReloadToken, () => fired = true);

        await source.Provider.WriteAsync("Auth:RegistrationCode", "hunter2", CancellationToken.None);

        Assert.True(fired);
    }

    [Fact]
    public async Task A_blank_write_removes_the_key_rather_than_storing_emptiness()
    {
        const string machine = "cfg-clear";
        (IConfigurationRoot? config, MachineSettingsConfigurationSource? source) = BuildConfig(machine);
        Assert.NotNull(source.Provider);
        await source.Provider.WriteAsync("Auth:RegistrationCode", "code", CancellationToken.None);
        Assert.Equal("code", config["Auth:RegistrationCode"]);

        await source.Provider.WriteAsync("Auth:RegistrationCode", null, CancellationToken.None);

        // Unset and set-to-nothing have to stay the same state: the required-key check and the first-boot flow both
        // read "is there a value", and an empty string that counts as a value would skip setup on an unset box.
        Assert.Null(config["Auth:RegistrationCode"]);
        IReadOnlyDictionary<string, string> stored = await _db.MachineSettings.AllAsync(machine, CancellationToken.None);
        Assert.DoesNotContain("Auth:RegistrationCode", stored.Keys);
    }

    [Fact]
    public async Task Settings_are_scoped_to_the_machine_that_stored_them()
    {
        // One database can back several instances, and the renderer's address is a property of the box.
        await _db.MachineSettings.SetAsync("box-a", "ComfyUI:BaseUrl", "http://a:8188", CancellationToken.None);
        await _db.MachineSettings.SetAsync("box-b", "ComfyUI:BaseUrl", "http://b:8188", CancellationToken.None);

        Assert.Equal("http://a:8188", BuildConfig("box-a").Config["ComfyUI:BaseUrl"]);
        Assert.Equal("http://b:8188", BuildConfig("box-b").Config["ComfyUI:BaseUrl"]);
    }

    [Fact]
    public async Task Setting_the_same_key_twice_replaces_it()
    {
        const string machine = "cfg-replace";
        await _db.MachineSettings.SetAsync(machine, "Logging:FilePath", "one", CancellationToken.None);
        await _db.MachineSettings.SetAsync(machine, "Logging:FilePath", "two", CancellationToken.None);

        IReadOnlyDictionary<string, string> stored = await _db.MachineSettings.AllAsync(machine, CancellationToken.None);
        Assert.Equal("two", stored["Logging:FilePath"]);
    }

    [Fact]
    public void Every_spec_key_is_unique_and_findable()
    {
        List<string> keys = MachineSettingSpecs.All.Select(s => s.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(keys, k => Assert.NotNull(MachineSettingSpecs.Find(k)));
        // The settings API refuses anything not on the list, so an unknown key must not resolve.
        Assert.Null(MachineSettingSpecs.Find("Something:Else"));
    }

    [Fact]
    public void Every_toggle_says_what_it_means_unset()
    {
        // A checkbox has no empty state to render: an unset key without a declared default draws as OFF, which is a
        // claim about the app's behaviour that nothing checked. Making it required at the spec keeps the form and
        // the code that reads the key on the same answer.
        Assert.All(MachineSettingSpecs.All.Where(s => s.Kind == SettingKind.Bool),
            s => Assert.True(bool.TryParse(s.Default, out _), $"{s.Key} declares no default."));
    }

    [Fact]
    public async Task An_unset_toggle_reads_as_its_declared_default()
    {
        const string machine = "cfg-bool";
        (IConfigurationRoot? config, MachineSettingsConfigurationSource? source) = BuildConfig(machine);
        Assert.NotNull(source.Provider);

        Assert.Null(config["Updates:Enabled"]);
        Assert.True(config.IsOn("Updates:Enabled"));

        // A stored value still wins over the default, in both directions.
        await source.Provider.WriteAsync("Updates:Enabled", "false", CancellationToken.None);
        Assert.False(config.IsOn("Updates:Enabled"));
        await source.Provider.WriteAsync("Updates:Enabled", "true", CancellationToken.None);
        Assert.True(config.IsOn("Updates:Enabled"));
    }
}
