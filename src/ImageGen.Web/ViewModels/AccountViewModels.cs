namespace ImageGen.Web.ViewModels;

public sealed class LoginViewModel
{
    public string Username { get; set; } = "";
    public string? ReturnUrl { get; set; }
    public string? Error { get; set; }
}

public sealed class RegisterViewModel
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ReturnUrl { get; set; }
    public bool RequiresCode { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// True when this box has no accounts at all and the visitor was sent here rather than choosing to register.
    /// The page says so: on a fresh install a sign-in form is a dead end, and "wrong username or password" is a
    /// misleading answer to "there is nobody to sign in as".
    /// </summary>
    public bool IsFirstAccount { get; set; }
}
