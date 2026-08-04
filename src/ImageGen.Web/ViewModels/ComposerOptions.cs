namespace ImageGen.Web.ViewModels;

/// <summary>
/// Parameters for the shared composer partial (_Composer.cshtml), so the compose page and the artist page
/// render the same generation box. When <see cref="LockedArtist"/> is set the box is in "artist mode":
/// every generation is locked to that artist, and the Random-artist option and the '@' artist autocomplete
/// are hidden (compose.js reads the locked artist from the box's data-artist attribute).
/// </summary>
public sealed class ComposerOptions
{
    /// <summary>The canonical artist token to lock generations to, or null for the normal (compose-page) box.</summary>
    public string? LockedArtist { get; init; }
}
