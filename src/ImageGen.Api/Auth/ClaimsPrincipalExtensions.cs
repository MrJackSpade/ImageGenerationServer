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
}
