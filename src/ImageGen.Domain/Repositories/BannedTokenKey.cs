//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Domain.Repositories;

/// <summary>
/// The identity of a single banned token — the unique key <c>(UserId, ModelId, Name, Kind)</c> used to remove a
/// ban. <see cref="Name"/> is canonical (lowercase, underscored), matching the marks map.
/// </summary>
/// <param name="UserId">The user who owns the ban.</param>
/// <param name="ModelId">The configuration id the ban applies to.</param>
/// <param name="Name">The canonical token name.</param>
/// <param name="Kind">Whether the token is a tag or an artist.</param>
public sealed record BannedTokenKey(long UserId, string ModelId, string Name, TokenKind Kind);
