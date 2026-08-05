using ImageGen.Web.Configuration;

namespace ImageGen.Web.Hosting;

/// <summary>
/// While a required machine setting is unset, send every page request to /setup.
///
/// <para>An unconfigured box cannot render anything, so the alternative is a working-looking app that fails at the
/// first generation with a connection error — a worse answer to "what is wrong" than a form asking the question.</para>
///
/// <para>The moment the required keys have values this stops matching, which is what makes the anonymous setup page
/// temporary rather than a permanent hole. Static files and the drain probe pass through: the deploy script polls
/// the probe against an app that may be mid-configuration, and the setup page needs its own stylesheet.</para>
/// </summary>
public sealed class SetupRequiredMiddleware(RequestDelegate next, MachineConfigService machine)
{
    /// <summary>Route segment for the anonymous setup page (also the redirect target).</summary>
    private const string SetupPath = "/setup";

    /// <summary>Route segment for the drain probe the deploy script polls.</summary>
    private const string DrainStatusPath = "/drain-status";

    /// <summary>Route segment for stylesheets.</summary>
    private const string CssPath = "/css";

    /// <summary>Route segment for scripts.</summary>
    private const string JsPath = "/js";

    /// <summary>Route segment for the favicon.</summary>
    private const string FaviconPath = "/favicon.ico";

    /// <summary>Route segment prefix for JSON API callers.</summary>
    private const string ApiPath = "/api";

    /// <summary>Route segment prefix for the forge endpoints.</summary>
    private const string ForgePath = "/forge";

    private readonly RequestDelegate _next = next;
    private readonly MachineConfigService _machine = machine;

    public async Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;
        bool exempt =
            path.StartsWithSegments(SetupPath) ||
            path.StartsWithSegments(DrainStatusPath) ||
            path.StartsWithSegments(CssPath) ||
            path.StartsWithSegments(JsPath) ||
            path.StartsWithSegments(FaviconPath);

        if (exempt || _machine.IsConfigured)
        {
            await _next(context);
            return;
        }

        // An API caller gets an error it can read, not a redirect into a page it cannot render.
        if (path.StartsWithSegments(ApiPath) || path.StartsWithSegments(ForgePath))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "This box has not been configured yet — the renderer's address is unset. Open /setup.",
            });
            return;
        }

        context.Response.Redirect(SetupPath);
    }
}
