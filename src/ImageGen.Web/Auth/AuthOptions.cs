namespace ImageGen.Web.Auth;

/// <summary>
/// Auth configuration for local accounts, read live.
///
/// <para>The code used to be captured into this object at startup, so changing it meant restarting the app. It is a
/// machine setting now (Auth:RegistrationCode), stored in the database and editable from the settings page, so this
/// reads it on each check instead of holding a copy.</para>
/// </summary>
public sealed class AuthOptions(IConfiguration configuration)
{
    private readonly IConfiguration _configuration = configuration;

    /// <summary>
    /// Shared code required to register a new account. Null means open registration — "no code has been set" is the
    /// honest meaning of an unset setting, carried by the nullable itself rather than by a "" the app would have to
    /// re-interpret. When present, registration must supply this code (it protects the internet-reachable endpoint).
    /// </summary>
    public string? RegistrationCode => _configuration["Auth:RegistrationCode"];

    public bool RegistrationRequiresCode => !string.IsNullOrEmpty(RegistrationCode);
}
