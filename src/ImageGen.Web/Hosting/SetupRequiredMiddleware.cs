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
    private readonly RequestDelegate _next = next;
    private readonly MachineConfigService _machine = machine;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var exempt =
            path.StartsWithSegments("/setup") ||
            path.StartsWithSegments("/drain-status") ||
            path.StartsWithSegments("/css") ||
            path.StartsWithSegments("/js") ||
            path.StartsWithSegments("/favicon.ico");

        if (exempt || _machine.IsConfigured)
        {
            await _next(context);
            return;
        }

        // An API caller gets an error it can read, not a redirect into a page it cannot render.
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/forge"))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "This box has not been configured yet — the renderer's address is unset. Open /setup.",
            });
            return;
        }

        context.Response.Redirect("/setup");
    }
}
