namespace ImageGen.Domain;

/// <summary>The media file extensions (with the leading dot) the app recognises when classifying a rendered or
/// downloaded file as image vs. video. Named once here so a suffix test (<c>EndsWith(".mp4")</c>) or a
/// content-type map never re-spells the literal.</summary>
public static class MediaFileExtensions
{
    public const string Mp4 = ".mp4";
    public const string Webm = ".webm";
    public const string Webp = ".webp";
    public const string Png = ".png";
    public const string Gif = ".gif";
}
