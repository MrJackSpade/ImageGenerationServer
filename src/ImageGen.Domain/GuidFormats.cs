namespace ImageGen.Domain;

/// <summary>Standard <see cref="System.Guid.ToString(string)"/> format specifiers, named so an id's shape is
/// declared once rather than re-spelled as a bare <c>"N"</c> at each generation site.</summary>
public static class GuidFormats
{
    /// <summary>32 hex digits with no dashes or braces (<c>"N"</c>) — the shape of a generated image/job id.</summary>
    public const string NoDashes = "N";
}
