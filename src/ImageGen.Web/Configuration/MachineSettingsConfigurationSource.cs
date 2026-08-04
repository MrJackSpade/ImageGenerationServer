//TODO: CHECK FOR FALLBACKS
using ImageGen.Domain.Repositories;

namespace ImageGen.Web.Configuration;

/// <summary>
/// Puts this machine's stored settings into <see cref="IConfiguration"/> as an ordinary configuration source, so
/// every existing <c>config["ComfyUI:BaseUrl"]</c> read keeps working and picks up a change without a restart.
///
/// <para>The rows ARE the configuration: <c>SettingKey</c> is the configuration path exactly as IConfiguration
/// spells it. There is deliberately no merge with the JSON file — a key lives in the database or in the file, never
/// in both, and everything movable has been removed from the file. The file keeps only what is needed to open this
/// database, which is the one thing that cannot be stored inside it.</para>
///
/// <para>If the table is missing the load throws and the app does not start. That is the intended outcome: a box
/// whose schema has not been applied would otherwise boot on whatever the code happened to default to, which is
/// precisely the two-sources-of-truth problem this replaces.</para>
/// </summary>
public sealed class MachineSettingsConfigurationSource(IMachineSettingRepository repository, string machineName)
    : IConfigurationSource
{
    /// <summary>The live provider, once the configuration root has built it. Writers reload through this.</summary>
    public MachineSettingsConfigurationProvider? Provider { get; private set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        Provider = new MachineSettingsConfigurationProvider(repository, machineName);
}

/// <summary>The provider half of <see cref="MachineSettingsConfigurationSource"/>. See there for the rules.</summary>
public sealed class MachineSettingsConfigurationProvider(IMachineSettingRepository repository, string machineName)
    : ConfigurationProvider
{
    private readonly IMachineSettingRepository _repository = repository;
    private readonly string _machineName = machineName;

    /// <summary>The machine these settings belong to — shown in the UI, since a shared database holds several.</summary>
    public string MachineName => _machineName;

    /// <summary>
    /// Blocking, because configuration sources load synchronously while the host is being built. This runs once at
    /// startup and again after each write; it is a handful of rows.
    /// </summary>
    public override void Load() =>
        Data = _repository.AllAsync(_machineName, CancellationToken.None).GetAwaiter().GetResult()
            .ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Write a key (null removes it), then re-read and fire the change token. Everything that reads the value
    /// through IConfiguration sees the new one on its next read — which is why the consumers that used to snapshot
    /// their options at startup now read per use.
    /// </summary>
    public async Task WriteAsync(string key, string? value, CancellationToken ct)
    {
        await _repository.SetAsync(_machineName, key, value, ct);
        Load();
        OnReload();
    }
}
