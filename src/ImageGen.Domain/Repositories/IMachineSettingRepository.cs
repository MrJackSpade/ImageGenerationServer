namespace ImageGen.Domain.Repositories;

/// <summary>
/// This machine's own configuration, keyed by the configuration path it answers to ("ComfyUI:BaseUrl").
///
/// <para>Machine-scoped, not user-scoped: one database can back several app instances, and the renderer's address
/// is a property of the box. Nothing here is encrypted — these are facts about a machine, not a user's words, and
/// there is no owning user to key a cipher by.</para>
///
/// <para>A key lives here or in the configuration file, never in both. The file keeps only what is needed to open
/// this database; everything else is a row in this table and is absent from the file entirely.</para>
/// </summary>
public interface IMachineSettingRepository
{
    /// <summary>Every stored setting for the machine, keyed by configuration path.</summary>
    Task<IReadOnlyDictionary<string, string>> AllAsync(string machineName, CancellationToken ct);

    /// <summary>Store a value, or remove the key entirely when <paramref name="value"/> is null.</summary>
    Task SetAsync(string machineName, string key, string? value, CancellationToken ct);
}
