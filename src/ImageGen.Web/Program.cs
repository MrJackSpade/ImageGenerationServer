using ImageGen.Api;
using ImageGen.Application;
using ImageGen.Application.Platform;
using ImageGen.Application.Rendering;
using ImageGen.Application.Tags;
using ImageGen.TagModel;
using ImageGen.Comfy;
using ImageGen.Comfy.Patches;
using ImageGen.Web.Comfy;
using ImageGen.Infrastructure;
using ImageGen.Infrastructure.Database;
using ImageGen.Infrastructure.Repositories;
using ImageGen.Web.Configuration;
using ImageGen.Media;
using ImageGen.Web.Auth;
using ImageGen.Web.Hosting;
using ImageGen.Web.Reconciler;
using ImageGen.Web.Updates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// --- this machine's configuration ------------------------------------------------------------------
// FIRST, before anything reads a setting. The appsettings file holds only what has to be known before the app can
// ask the database anything — how to reach the database, and how to bind the listener. Every other key in
// MachineSettingSpecs has been REMOVED from that file and lives in dbo.MachineSetting, keyed by machine name, so
// each one has exactly one home and "I edited the file and nothing changed" cannot happen.
//
// It is registered as an ordinary configuration source, so every existing config["..."] read keeps working — and
// because the provider reloads and fires its change token on write, the reads that happen PER USE (the renderer's
// address, the registration code, the memory floor, the stack-trace toggle) pick a change up without a restart.
// The ones consumed once while the host is built say so in the settings UI rather than pretending.
//
// A missing dbo.MachineSetting throws here and the app does not start. Deliberate: the alternative is booting on
// code defaults for values the operator believes they set, which is the failure this whole design removes. Under
// SQL Server apply schema.sql first — the app's login has no DDL rights, by design.
var connectionString = config.GetConnectionString("ImageGen")
    ?? throw new InvalidOperationException("Missing connection string 'ImageGen'.");

// Which engine. Defaults to Sqlite, which needs no server and no out-of-band schema step, so an unconfigured
// install starts and works. AddInfrastructure refuses to start if the provider and the connection string disagree,
// rather than quietly creating an empty database -- so a SQL Server deployment that sets a SQL Server connection
// string but no provider gets a named error at startup, not a silent second database.
var databaseProvider = Enum.TryParse<DatabaseProvider>(config["Database:Provider"], ignoreCase: true, out var parsed)
    ? parsed
    : DatabaseProvider.Sqlite;

var bootstrapConnections = InfrastructureServiceCollectionExtensions.CreateConnectionFactory(connectionString, databaseProvider);

// The schema comes BEFORE the settings are read, and both come before the host is built. Under SQLite the app is
// the only schema mechanism there is -- no server, no login, no elevated sqlcmd -- so a fresh install must create
// its own tables before anything reads one; this used to run after Build(), which a configuration source that
// queries the database turns into a install-can-never-start deadlock. Under SQL Server it stays off (the app's
// login holds no DDL rights) and a box whose schema has not been applied fails on the read below, by design.
// Either way an explicit Database:EnsureSchemaOnStartup wins.
if (config.GetValue("Database:EnsureSchemaOnStartup", databaseProvider == DatabaseProvider.Sqlite))
    await new DatabaseInitializer(bootstrapConnections, databaseProvider).EnsureSchemaAsync(CancellationToken.None);

var machineSettings = new MachineSettingsConfigurationSource(
    new MachineSettingRepository(bootstrapConnections), Environment.MachineName);
((IConfigurationBuilder)config).Add(machineSettings);

// --- log file ------------------------------------------------------------------------------------
// Logs went only to the Windows Application event log, which is awkward to read and easy to lose. They go to a file
// too now — but ONLY because the plaintext prompt sinks are gone and NoPlaintextLogTests fails the build if an
// ILogger call so much as looks like it emits a prompt-bearing value. A file is durable, greppable and outlives the
// process by years; that is the point of it, and the reason it had to come last.
//
// Nothing is deleted. There is deliberately no retained-file limit and no size cap: a cap is a number nobody chose
// that silently destroys the record you go looking for. Files roll by DAY so they stay readable, and pruning them is
// an operator's decision made with a broom, not a policy this app invents. Set Logging:FilePath to move them.
//
// The LEVEL is Serilog's own, read from the same Logging:LogLevel keys the rest of the app uses. This started as
// MinimumLevel.Verbose() on the assumption that the Logging:LogLevel filter would apply to this provider and it must
// not filter twice. It does not apply: the first deploy wrote 438 KB of Verbose/Debug ASP.NET internals in two
// minutes — a firehose that would fill the disk (there is deliberately no cap to stop it) and put request paths on
// disk at Debug, which is most of what the URL work was for. One source of truth, honoured here.
// The path is a machine setting now, so it is NOT in appsettings.json any more. The literal here is the value that
// file used to ship -- keeping it in code means an install that has never touched the setting logs exactly where it
// always did, rather than silently stopping. Blank the setting to turn the file sink off.
var logFilePath = config["Logging:FilePath"] ?? "logs/imagegen-.log";
if (!string.IsNullOrWhiteSpace(logFilePath))
{
    var logPath = Path.IsPathRooted(logFilePath) ? logFilePath : Path.Combine(builder.Environment.ContentRootPath, logFilePath);
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

    static Serilog.Events.LogEventLevel Level(string? configured, Serilog.Events.LogEventLevel fallback) =>
        Enum.TryParse<LogLevel>(configured, ignoreCase: true, out var mel) && mel != LogLevel.None
            ? (Serilog.Events.LogEventLevel)mel   // MEL Trace..Critical map 1:1 onto Serilog Verbose..Fatal
            : fallback;

    builder.Logging.AddSerilog(new Serilog.LoggerConfiguration()
        .MinimumLevel.Is(Level(config["Logging:LogLevel:Default"], Serilog.Events.LogEventLevel.Information))
        .MinimumLevel.Override("Microsoft", Level(config["Logging:LogLevel:Microsoft.AspNetCore"], Serilog.Events.LogEventLevel.Warning))
        .WriteTo.File(
            logPath,
            rollingInterval: Serilog.RollingInterval.Day,
            retainedFileCountLimit: null,   // keep every day. See above: no invented cap.
            fileSizeLimitBytes: null,
            shared: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .CreateLogger(), dispose: true);
}

// Options, bound from config into the internal option objects.
//
// The keys say what they configure: ComfyUI:* is the render backend, Catalog:* is the workflow catalogue, Media:* is
// ffmpeg. They were all Forge:* -- named after a project that no longer exists, which made "which of these is the
// ComfyUI address" a question you had to already know the history to answer. There is no compatibility read of the
// old names: a key that is silently accepted under two spellings is a key nobody can ever remove.
//
// Logging:LogPrompts is GONE, not merely defaulted off. It wrote the user's prompt (and the whole submitted workflow
// graph, which embeds it) to the plaintext app log; with a file sink that is one toggle from putting prompts on disk
// permanently. Prompt-bearing diagnostics go to the per-user ENCRYPTED log via Logging:AuditUserPrompts.
//
// Forge:ModelsDir and Tags:ArtistType are GONE, not merely defaulted. Nothing read ModelsDir at all, and ArtistType
// encoded a fact about the tag data (gelbooru artist == 1) as if it were a deployment choice -- config that nothing
// consumes, or that restates a constant, is a key an operator has to reason about for no reason.
//
// ComfyUI:BaseUrl and ComfyUI:GateToken are NOT here any more, and neither has a `?? default` behind it. They are
// read per use through IComfyEndpoint, so the settings page can move the renderer without a restart -- and the URL
// having no default is what lets first boot tell "nobody has configured this" from "somebody chose localhost".
//
// Catalog:Path is GONE, not merely defaulted. It named the directory the catalogue is in, and the catalogue ships
// WITH the application -- the release archive copies configurations/ next to the binary, so the only correct answer
// is the one below. Nothing in the repo, the deploy, or any box ever set it: it was a text box whose every valid
// value was already its default, and whose invalid values fail the boot.
var comfyOptions = new ComfyOptions
{
    CatalogPath = "configurations",
};
// ffmpeg runs IN-PROCESS (Loxifi.FFmpeg), so there is no executable to locate, no download step, and no path to
// configure. The defaults pick Cisco's OpenH264 from the LGPL runtime -- see MediaOptions for why x264 is not the
// default despite being the better encoder.
var mediaOptions = new MediaOptions();
var renderOptions = new RenderOptions();

// --- DI (composition root) -----------------------------------------------------------------------
builder.Services.AddSingleton<AuthOptions>();          // reads Auth:RegistrationCode live
builder.Services.AddSingleton(TimeProvider.System);

// The machine-settings source, and the service that reads and writes it. The source is registered as itself so the
// writer can reach the built provider; MachineConfigService is what the API and the setup flow talk to.
builder.Services.AddSingleton(machineSettings);
builder.Services.AddSingleton<MachineConfigService>();
builder.Services.AddSingleton<ComfyProbe>();   // "did anything answer at that address" — setup page and settings API

// The patches page. ComfyUI's INSTALLATION rather than its address: the app has only ever known the renderer as a
// URL, and a URL cannot say which directory this process may write to. All four read their configuration live, so
// pointing the box at a different ComfyUI needs no restart. PackSource/PatchInstaller are the engine the container
// build runs too (tools/ComfyPatch) — one implementation, so the build and the page cannot disagree about state.
builder.Services.AddSingleton<ComfyInstall>();
builder.Services.AddSingleton<ComfySupervisor>();
builder.Services.AddSingleton<PackSource>();
builder.Services.AddSingleton<PatchInstaller>();
builder.Services.AddSingleton<ComfyPatchService>();

// "Is there a newer release?" — cached and re-asked when a request finds the answer over an hour old (see
// UpdateCheck). Singleton so the one cached answer is shared across every request, not re-fetched per call.
builder.Services.AddSingleton(sp => new UpdateCheck(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<UpdateCheck>>(),
    AppVersion.Current));

// LoRA trigger-word / preview lookup against CivitAI by file hash. Opt-out (Civitai:Enabled), like the update check.
builder.Services.AddSingleton<ImageGen.Application.Civitai.ICivitaiClient, ImageGen.Web.Civitai.CivitaiClient>();

// Only the composition root knows what a configuration key is called, so the live endpoint lives here rather than
// in the Comfy adapter. Nothing is cached: a read is a dictionary lookup against the current configuration.
builder.Services.AddSingleton<IComfyEndpoint>(sp => new ConfiguredComfyEndpoint(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddInfrastructure(connectionString, databaseProvider);
builder.Services.AddApplication(renderOptions, config.GetValue("Logging:AuditUserPrompts", false));

// Admission control for work that will hold memory until it renders. Uploaded render inputs stay resident until their
// job runs and are never evicted (see IUploadStore), so a box below this floor refuses new submissions outright and
// tells the caller — the alternative, dropping inputs to make room, destroys work that was already accepted.
// Per-OS, because there is no managed way to ask "how much memory does this BOX have left" -- GC.GetGCMemoryInfo
// describes this process's heap, which is a different question. An unsupported OS throws at startup rather than
// guessing: this gate decides whether to accept work that will hold memory until it renders.
builder.Services.AddSingleton<ISystemMemory>(_ =>
    OperatingSystem.IsWindows() ? new WindowsSystemMemory()
    : OperatingSystem.IsLinux() ? new LinuxSystemMemory()
    : throw new PlatformNotSupportedException(
        $"no available-memory probe for {Environment.OSVersion.Platform}. Windows and Linux are implemented "
        + "(see ISystemMemory); the submission gate cannot run without one."));
// The floor is read per check, not captured, so raising it on the settings page takes effect on the next submission.
builder.Services.AddSingleton(sp => new SubmissionMemoryGate(
    sp.GetRequiredService<ISystemMemory>(),
    () => sp.GetRequiredService<IConfiguration>().GetValue("Uploads:MinAvailableMemoryMB", 500L) * 1024L * 1024L));
builder.Services.AddComfy(comfyOptions);
builder.Services.AddMedia(mediaOptions);

// The tag model, in-process. Serves BOTH tag ports -- '#'/'@' autocomplete and the random-artist pick (ITagCatalog),
// and context-ranked suggestions plus whole-prompt generation (ITagModelClient) -- from one loaded model. This replaces
// tags.json AND the separate Python service on port 8000; there is no Tags:ModelUrl any more because there is no URL.
// Loads at startup on purpose: a missing artifact breaks autocomplete and fails every random-prompt render, so
// refusing to start with the missing filename named beats serving a page whose tag box quietly does nothing.
//
// The app FETCHES the artifacts itself if they are not there. This was three copies of the same download --
// install.ps1, install.sh and the Docker entrypoint -- that a user had to know to run, or know not to run, before
// anything worked. All three are gone: the app knows it needs the file, so the app gets it.
using var startupLoggers = LoggerFactory.Create(b => b.AddConsole());
using (var artifactsHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })   // ~900 MB on a first run
    await TagModelArtifacts.EnsureAsync(
        artifactsHttp, startupLoggers.CreateLogger("TagModel"), CancellationToken.None);
builder.Services.AddTagModel();
builder.Services.AddMemoryCache();   // backs the /forge/image?w=N thumbnail + mp4 caches

builder.Services.AddControllersWithViews();

// Run the render orchestrator's background loop (the core orchestrator is a plain singleton; this adapts it to a
// hosted service). Registered here, alongside the singleton the AddApplication call created.
builder.Services.AddHostedService<RenderWorker>();

// Vestigial reconciler: reaps stale PendingJob rows (history is worker-written). Toggle off via Reconciler:Enabled.
if (config.IsOn("Reconciler:Enabled"))
    builder.Services.AddHostedService<PendingJobReconciler>();

// /forge/upload reads the posted file via ReadFormAsync, whose 128MB default would become the new
// binding limit once Kestrel's MaxRequestBodySize is raised past it. Keep it in step with the Kestrel
// limit (appsettings.json) and nginx's client_max_body_size (deploy/imagegen-nginx.conf).
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 536870912;
});

// Clearing the known-proxy lists makes the app honour X-Forwarded-* from ANY caller, which is what a single box behind
// its own trusted reverse proxy wants and how this has always run. It is also spoofable by anyone who can reach the app
// directly, so a packaged install that is exposed differently needs to be able to say no. Defaults to the historical
// behaviour; set Security:TrustAllProxies=false to keep ASP.NET's loopback-only default instead.
var trustAllProxies = config.IsOn("Security:TrustAllProxies");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    if (trustAllProxies)
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "imagegen_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/account/login";
        // JSON API callers want a 401; browser page requests get the normal login redirect.
        options.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        // A valid signature is not a valid session. The identity does NOT live in the cookie — the cookie carries an
        // opaque session key and MemoryTicketStore (wired in as SessionStore just below) holds the ticket server-side.
        // So a cookie is a handle to a session, not a self-contained "I am user 1" assertion that keeps meaning that
        // for as long as its signature verifies. That is what closes the ghost-cookie hole: the Data Protection keys
        // that sign the cookie live in the OS user profile, not the database, so wiping the database (or reinstalling)
        // used to leave a perfectly-signed cookie for a user that no longer exists — and checking "does a user with
        // this id still exist" did not save it, because ids are BIGINT IDENTITY and a re-created first account retakes
        // id 1, so the ghost authenticated as whoever now held its id. A session key names a row in the store; after a
        // restart (which a wipe or a redeploy is) there is no such row, so the request is anonymous and login runs.
    });

// Server-side session state for the cookie above. Singleton (it owns an in-process cache); post-configured onto the
// cookie options here, after DI is built, because the AddCookie callback runs before the provider that holds it exists.
builder.Services.AddSingleton<MemoryTicketStore>();
builder.Services
    .AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<MemoryTicketStore>((options, store) => options.SessionStore = store);

builder.Services.AddAuthorization();

// Move off a port something else already holds, rather than refusing to start. Kestrel's own answer to a taken
// port is to throw, and on a desktop box that almost always means the app is already running or something grabbed
// 8080 -- a failure the user can do nothing with at the moment they hit it. The next port up is used instead and
// said out loud, here and in Kestrel's own "Now listening on" line.
//
// NOTE for a proxied deployment: nginx forwards to a fixed port, so an app that quietly moved is an app the proxy
// can no longer reach. The warning is the only signal; pin the port and keep it free if that matters.
var configuredUrls = config["Urls"];
var listenUrls = ListenAddress.Resolve(
    configuredUrls,
    onMoved: (host, wanted, actual) => startupLoggers.CreateLogger("Startup").LogWarning(
        "Port {Wanted} on {Host} is already in use; listening on {Actual} instead.", wanted, host, actual));
if (listenUrls != configuredUrls) config["Urls"] = listenUrls;

var app = builder.Build();

// (The schema is applied at the top of this file now, before the machine settings are read out of it.)

// Warm the booru tag store at startup (it loads its large file once in the background, not on the first /forge/tags hit).
app.Services.GetRequiredService<ITagCatalog>();

// --- pipeline ------------------------------------------------------------------------------------
// Outermost middleware: turn any unhandled exception into a 500 whose JSON body carries the full stack trace
// (type + message + inner exceptions, via Exception.ToString()) instead of ASP.NET's bodiless generic 500. The
// SPA reads `error` off the body (core.js gwError), so the real failure surfaces in the UI and to API callers.
// On an internal single-box tool exposing traces is intentional, not a leak, and that stays the default.
//
// Set Diagnostics:ExposeStackTraces=false where the app is reachable by people who shouldn't read its internals: the
// shape of the body is unchanged (`error` is still there, so the SPA keeps working) -- it just carries the exception
// MESSAGE instead of the full trace. The trace still reaches the log file either way, so turning this off costs
// diagnosability at the browser, not in the record.
// Read per failure rather than captured at startup: it is a machine setting, and the moment you want to change it
// is the moment something is going wrong, which is the worst possible time to need a restart.
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var exposeStackTraces = app.Configuration.IsOn("Diagnostics:ExposeStackTraces");
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new
    {
        error = (exposeStackTraces ? ex?.ToString() : ex?.Message)
            ?? "Unhandled server error (no exception captured).",
        type = exposeStackTraces ? ex?.GetType().FullName : null,
    });
}));

app.UseForwardedHeaders();
app.UseStaticFiles();
// Before auth, because a box that has never been configured has no accounts to authenticate against — the setting
// that gates registration is one of the ones being asked for. Stops matching as soon as they are set.
app.UseMiddleware<SetupRequiredMiddleware>();
app.UseWebSockets();   // for /forge/ws (live progress)
app.UseAuthentication();
// After the cookie handler, before authorization: a per-user API key (AppUser.ApiKey, sent as X-Api-Key or
// Authorization: Bearer) stands in for the login cookie, so API apps can act as a specific user. No-op for browsers.
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapImageGenApi();   // /api (client actions) + /forge (render backend)

// Ops/deploy drain probe (anonymous, root — like a health check). Reports how much render work is in flight so
// _redeploy.ps1 can wait it out before stopping the app. Slots merely waiting re-hydrate and resume, so the gate keys
// on work in flight, not queue depth. Exposes only counts — no prompts, no user data.
app.MapGet("/drain-status", (RenderOrchestrator queue) =>
{
    var w = queue.Workload();
    return Results.Ok(new
    {
        activeJobs = w.ActiveJobs,
        // What the drain waits out: the slot the worker holds, from pick to resolution. Broader than "on the GPU" on
        // purpose — stopping mid-prompt-build, or while the backend has the prompt queued, orphans it just as badly.
        inFlightSlots = w.InFlightSlots,
        // The legacy name the deploy script keys on, kept identical to inFlightSlots and never dropped: a deploy runs
        // the NEW script against the OLD app and vice-versa, and a script reading a field this payload no longer had
        // would read 0 and stop the app mid-render.
        runningSlots = w.InFlightSlots,
        // On the GPU right now — the narrow fact a user is shown as "running" (0 or 1).
        executingSlots = w.ExecutingSlots,
        queuedSlots = w.WaitingSlots,
    });
});

// Start, then print an address a person can actually click. Kestrel's own line reports the BIND address, and
// http://0.0.0.0:8080 is not a thing you can open — it means "every interface", and pasting it into a browser
// fails. The bound addresses are only known after the server is listening, which is also what makes this the
// honest place to print the port when it moved off a taken one.
await app.StartAsync();

string? firstReachable = null;
foreach (var address in app.Urls)
{
    var reachable = StartupBrowser.Reachable(address);
    firstReachable ??= reachable;
    Console.WriteLine();
    // ASCII only. This is the one line a user is told to read, and a Windows console on a non-UTF-8 code page
    // turns anything else into mojibake.
    Console.WriteLine($"  ImageGen is running - open {reachable}");
    Console.WriteLine();
}

// And open it, if the launcher asked. Only the launcher sets this, so a container, a service or a scheduled task
// is left alone — see StartupBrowser for why that is the trigger rather than a guess at whether a desktop exists.
if (firstReachable is not null && StartupBrowser.Requested(Environment.GetEnvironmentVariable(StartupBrowser.EnvVar)))
    StartupBrowser.Open(firstReachable, startupLoggers.CreateLogger("Startup"));

await app.WaitForShutdownAsync();

/// <summary>Exposed so the test project can spin up the app via WebApplicationFactory.</summary>
public partial class Program;
