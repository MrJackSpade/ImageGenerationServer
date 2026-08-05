using System.Security.Claims;
using ImageGen.Domain.Repositories;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ImageGen.Web.Auth;

/// <summary>
/// Authenticates a non-browser request from a per-user API key (the <c>AppUser.ApiKey</c> GUID), so API apps can act
/// as a specific user without a login cookie. The key may be sent as the <c>X-Api-Key</c> header or an
/// <c>Authorization: Bearer &lt;key&gt;</c> header. Runs only when the request isn't already cookie-authenticated; an
/// absent/blank/unknown key is ignored and the request stays anonymous (normal authorization then rejects it). The
/// principal it builds is shaped exactly like the login cookie's (NameIdentifier = user id, Name = display name), so
/// every downstream check — <c>RequireAuthorization()</c> on /api, the /forge gate, <see cref="ClaimsPrincipalExtensions.GetUserId"/>
/// — treats the caller as that user. Placed after UseAuthentication and before UseAuthorization.
/// </summary>
public sealed class ApiKeyAuthenticationMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Api-Key";

    /// <summary><see cref="HttpContext.Items"/> key marking a request as API-key (non-browser) authenticated.</summary>
    private const string AuthViaApiKeyItemKey = "AuthViaApiKey";

    /// <summary>The <c>Authorization: Bearer &lt;key&gt;</c> scheme prefix.</summary>
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context, IUserRepository users)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var key = ExtractKey(context.Request);
            if (!string.IsNullOrWhiteSpace(key))
            {
                var user = await users.GetByApiKeyAsync(key, context.RequestAborted);
                if (user is not null)
                {
                    var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
                    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
                    identity.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName));
                    context.User = new ClaimsPrincipal(identity);
                    // Marks this request as API-key (non-browser) authenticated so downstream can scope by caller —
                    // e.g. /forge/workflows serves the API-visible list to these callers and the UI-visible list to cookies.
                    context.Items[AuthViaApiKeyItemKey] = true;
                }
            }
        }

        await _next(context);
    }

    /// <summary>Pull the key from the <c>X-Api-Key</c> header, falling back to <c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    private static string? ExtractKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderName, out var headerVal) && !string.IsNullOrWhiteSpace(headerVal))
            return headerVal.ToString().Trim();

        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return auth[BearerPrefix.Length..].Trim();

        return null;
    }
}
