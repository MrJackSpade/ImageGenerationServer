using System.Security.Claims;

namespace ImageGen.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated user's database id, from the app cookie's NameIdentifier claim.</summary>
    public static long? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(value, out var id) ? id : null;
    }

    /// <summary>The authenticated user's database id, guaranteed. Throws when the principal carries no valid id —
    /// every caller sits behind authentication, so a missing id is a broken invariant, not a case to handle.</summary>
    public static long GetRequiredUserId(this ClaimsPrincipal principal)
        => principal.GetUserId() ?? throw new InvalidOperationException("The principal has no authenticated user id.");
}
