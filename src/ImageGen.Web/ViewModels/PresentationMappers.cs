using ImageGen.Domain;
using ImageGen.Domain.Entities;

namespace ImageGen.Web.ViewModels;

/// <summary>
/// Hand-written projection from domain entities to the presentation DTOs the Razor views bind to. No AutoMapper —
/// every field is mapped by name here, so the entity→view boundary is explicit and the entities stay out of the views.
/// </summary>
public static class PresentationMappers
{
    /// <summary>Project a history entry to its grid-card view. <paramref name="viewed"/> is the set of image ids the
    /// user has opened; anything absent from it renders outlined as unviewed.</summary>
    public static HistoryItemView ToItemView(this HistoryEntry e, IReadOnlySet<string> viewed) =>
        new(e.GatewayImageId, e.Prompt, e.ModelFriendly,
            new DateTimeOffset(DateTime.SpecifyKind(e.CreatedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            viewed.Contains(e.GatewayImageId));

    /// <summary>Project a bookmarked image to its grid-card view.</summary>
    public static ImageBookmarkView ToBookmarkView(this ImageBookmark b) =>
        new(b.GatewayImageId, b.Prompt, b.ModelFriendly, b.ModelId, b.Aspect,
            new DateTimeOffset(DateTime.SpecifyKind(b.OriginalCreatedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            MarksToMap(b.Marks));

    /// <summary>Project a history entry to the detail/card view (marks flattened to the SPA's token→kind shape).</summary>
    public static ImageDetailView ToDetailView(this HistoryEntry e) => new(
        e.GatewayImageId, e.Prompt, e.ModelFriendly, e.ModelId, e.Aspect, e.CreatedAtUtc, MarksToMap(e.Marks));

    private static IReadOnlyDictionary<string, string> MarksToMap(IReadOnlyList<Mark> marks) =>
        marks.ToDictionary(m => m.Token, m => m.Kind.ToWire());
}
