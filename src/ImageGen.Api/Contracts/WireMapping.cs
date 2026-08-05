using ImageGen.Application.Models;
using ImageGen.Domain;
using ImageGen.Domain.Entities;

namespace ImageGen.Api.Contracts;

/// <summary>
/// Explicit, hand-written mapping between the wire contracts and the Application/Domain types.
/// No AutoMapper — every field is mapped by name here so the boundary is visible and intentional.
/// </summary>
public static class WireMapping
{
    #region primitives

    public static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static TokenKind ParseKind(string kind) => TokenKindWire.Parse(kind);

    public static string KindToString(TokenKind kind) => kind.ToWire();

    public static IReadOnlyList<Mark> MarksFromMap(Dictionary<string, string>? marks)
    {
        if (marks is null || marks.Count == 0)
            return [];
        return marks.Select(kv => new Mark(kv.Key, ParseKind(kv.Value))).ToList();
    }

    public static Dictionary<string, string>? MarksToMap(IReadOnlyList<Mark> marks)
    {
        if (marks.Count == 0)
            return null;
        return marks.ToDictionary(m => m.Token, m => KindToString(m.Kind));
    }

    #endregion

    #region history

    public static AddHistoryCommand ToAddHistoryCommand(this HistoryRecordContract c, long userId) => new()
    {
        UserId = userId,
        GatewayImageId = c.Id,
        Prompt = c.Prompt,
        ModelFriendly = c.Model,
        ModelId = c.ModelId,
        Aspect = c.Aspect,
        CreatedAtUtc = FromMs(c.Ts),
        Marks = MarksFromMap(c.Marks),
    };

    /// <param name="viewed">Image ids this user has opened; anything absent renders as unviewed.</param>
    public static HistoryRecordContract ToContract(this HistoryEntry e, IReadOnlySet<string>? viewed = null) => new()
    {
        Ts = ToMs(e.CreatedAtUtc),
        Id = e.GatewayImageId,
        Prompt = e.Prompt,
        Model = e.ModelFriendly,
        ModelId = e.ModelId,
        Aspect = e.Aspect,
        Marks = MarksToMap(e.Marks),
        Viewed = viewed is not null && viewed.Contains(e.GatewayImageId),
    };

    #endregion

    #region pending jobs

    public static RegisterPendingJobCommand ToRegisterPendingJobCommand(
        this PendingJobContract c, long userId, DateTime createdAtUtc) => new()
        {
            UserId = userId,
            JobId = c.JobId,
            Prompt = c.Prompt,
            ModelFriendly = c.Model,
            ModelId = c.ModelId,
            Aspect = c.Aspect,
            CreatedAtUtc = createdAtUtc,
        };

    public static PendingJobView ToView(this PendingJob e) => new()
    {
        JobId = e.JobId,
        Ts = ToMs(e.CreatedAtUtc),
        Prompt = e.Prompt,
        Model = e.ModelFriendly,
        ModelId = e.ModelId,
        Aspect = e.Aspect,
    };

    #endregion

    #region image bookmarks

    public static AddImageBookmarkCommand ToAddImageCommand(this ImageBookmarkContract c, long userId) => new()
    {
        UserId = userId,
        GatewayImageId = c.Id,
        Prompt = c.Prompt,
        ModelFriendly = c.Model,
        ModelId = c.ModelId,
        Aspect = c.Aspect,
        OriginalCreatedAtUtc = FromMs(c.Ts),
        Marks = MarksFromMap(c.Marks),
    };

    public static ImageBookmarkContract ToContract(this ImageBookmark b) => new()
    {
        Ts = ToMs(b.OriginalCreatedAtUtc),
        Id = b.GatewayImageId,
        Prompt = b.Prompt,
        Model = b.ModelFriendly,
        ModelId = b.ModelId,
        Aspect = b.Aspect,
        Marks = MarksToMap(b.Marks),
        SavedAt = ToMs(b.SavedAtUtc),
    };

    #endregion
}
