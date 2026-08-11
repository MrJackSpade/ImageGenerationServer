using ImageGen.Api;
using ImageGen.Application;
using ImageGen.Application.Platform;
using ImageGen.Application.Rendering;
using ImageGen.Application.Tags;
using ImageGen.Comfy;
using ImageGen.Domain.Repositories;
using ImageGen.Comfy.Patches;
using ImageGen.Infrastructure;
using ImageGen.Infrastructure.Database;
using ImageGen.Infrastructure.Repositories;
using ImageGen.Media;
using ImageGen.TagModel;
using ImageGen.Web.Auth;
using ImageGen.Web.Comfy;
using ImageGen.Web.Configuration;
using ImageGen.Web.Hosting;
using ImageGen.Web.Reconciler;
using ImageGen.Web.Updates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

/// <summary>Exposed so the test project can spin up the app via WebApplicationFactory.</summary>
public partial class Program
{
    /// <summary>Configuration keys this host reads. Each setting has exactly one spelling here so a rename reaches
    /// every read and no two spellings can drift.</summary>
    private static class ConfigKeys
    {
        /// <summary>Name of the <c>ImageGen</c> connection string, resolved via <c>GetConnectionString</c>.</summary>
        public const string ConnectionStringName = "ImageGen";

        /// <summary>Which database engine to use; unset defaults to Sqlite.</summary>
        public const string DatabaseProvider = "Database:Provider";

        /// <summary>Whether to create/upgrade the schema at startup.</summary>
        public const string EnsureSchemaOnStartup = "Database:EnsureSchemaOnStartup";

        /// <summary>Where the rolling log file is written; blank turns the file sink off.</summary>
        public const string LoggingFilePath = "Logging:FilePath";

        /// <summary>Default minimum log level for the file sink.</summary>
        public const string LogLevelDefault = "Logging:LogLevel:Default";

        /// <summary>Minimum log level applied to the Microsoft/ASP.NET source context.</summary>
        public const string LogLevelMicrosoftAspNetCore = "Logging:LogLevel:Microsoft.AspNetCore";

        /// <summary>Whether prompt-bearing diagnostics go to the per-user encrypted log.</summary>
        public const string AuditUserPrompts = "Logging:AuditUserPrompts";

        /// <summary>Available-memory floor, in MB, below which submissions are refused.</summary>
        public const string MinAvailableMemoryMB = "Uploads:MinAvailableMemoryMB";

        /// <summary>Whether the tag model downloads/loads its artifacts from the machine-wide SHARED cache
        /// (<c>%LOCALAPPDATA%\ImageGenerationServer\tagmodel\artifacts</c>). Unset/false → the pre-cache location beside
        /// the executable. Read at startup, so it lives in appsettings (and honours the <c>TagModel__WriteToSharedCache</c>
        /// environment variable) rather than <c>dbo.MachineSetting</c>.</summary>
        public const string TagModelWriteToSharedCache = "TagModel:WriteToSharedCache";

        /// <summary>Whether the stale-<c>PendingJob</c> reconciler runs.</summary>
        public const string ReconcilerEnabled = "Reconciler:Enabled";

        /// <summary>Whether <c>X-Forwarded-*</c> is honoured from any caller.</summary>
        public const string TrustAllProxies = "Security:TrustAllProxies";

        /// <summary>The Kestrel listen address(es).</summary>
        public const string Urls = "Urls";

        /// <summary>Whether unhandled-exception responses carry the full stack trace.</summary>
        public const string ExposeStackTraces = "Diagnostics:ExposeStackTraces";
    }

    /// <summary>Serilog wiring: the source-override prefix, logger category names, and the file output template.</summary>
    private static class LogNames
    {
        /// <summary>Source-context prefix whose minimum level the ASP.NET override sets.</summary>
        public const string MicrosoftSource = "Microsoft";

        /// <summary>Logger category for the tag-model artifact bootstrap.</summary>
        public const string TagModelCategory = "TagModel";

        /// <summary>Logger category for startup and listen-address messages.</summary>
        public const string StartupCategory = "Startup";

        /// <summary>The file sink's output template.</summary>
        public const string OutputTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
    }

    /// <summary>Fixed route paths mapped or matched in the pipeline.</summary>
    private static class Routes
    {
        /// <summary>The JSON API prefix; a login redirect becomes a 401 under it.</summary>
        public const string ApiPrefix = "/api";

        /// <summary>The anonymous deploy-drain probe.</summary>
        public const string DrainStatus = "/drain-status";
    }

    private static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        ConfigurationManager config = builder.Configuration;

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
        string connectionString = config.GetConnectionString(ConfigKeys.ConnectionStringName)
            ?? throw new InvalidOperationException("Missing connection string 'ImageGen'.");

        // Which engine. Defaults to Sqlite, which needs no server and no out-of-band schema step, so an unconfigured
        // install starts and works. AddInfrastructure refuses to start if the provider and the connection string disagree,
        // rather than quietly creating an empty database -- so a SQL Server deployment that sets a SQL Server connection
        // string but no provider gets a named error at startup, not a silent second database.
        // Unset defaults to Sqlite (the zero-config engine); a value that is SET but not a known provider is a typo, not a
        // request for the default — surfacing it beats silently starting on a different (empty) database than intended.
        string? providerConfigured = config[ConfigKeys.DatabaseProvider];
        const string providerListSeparator = ", ";
        DatabaseProvider databaseProvider = string.IsNullOrWhiteSpace(providerConfigured)
            ? DatabaseProvider.Sqlite
            : Enum.TryParse(providerConfigured, ignoreCase: true, out DatabaseProvider parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"Database:Provider is '{providerConfigured}', which is not a known provider — expected one of "
                    + $"{string.Join(providerListSeparator, Enum.GetNames<DatabaseProvider>())} (unset defaults to Sqlite).");

        IDbConnectionFactory bootstrapConnections = InfrastructureServiceCollectionExtensions.CreateConnectionFactory(connectionString, databaseProvider);

        // The schema comes BEFORE the settings are read, and both come before the host is built. Under SQLite the app is
        // the only schema mechanism there is -- no server, no login, no elevated sqlcmd -- so a fresh install must create
        // its own tables before anything reads one; running it after Build() would deadlock the install, because a
        // configuration source that queries the database cannot load before its tables exist. Under SQL Server it stays off
        // (the app's login holds no DDL rights) and a box whose schema has not been applied fails on the read below, by design.
        // Either way an explicit Database:EnsureSchemaOnStartup wins.
        if (config.GetValue(ConfigKeys.EnsureSchemaOnStartup, databaseProvider == DatabaseProvider.Sqlite))
        {
            await new DatabaseInitializer(bootstrapConnections, databaseProvider).EnsureSchemaAsync(CancellationToken.None);
        }

        MachineSettingsConfigurationSource machineSettings = new(
            new MachineSettingRepository(bootstrapConnections), Environment.MachineName);
        _ = ((IConfigurationBuilder)config).Add(machineSettings);

        // --- log file ------------------------------------------------------------------------------------
        // Logs go to a file as well as the Windows Application event log — but ONLY because the plaintext prompt sinks are
        // gone and NoPlaintextLogTests fails the build if an ILogger call so much as looks like it emits a prompt-bearing
        // value. A file is durable, greppable and outlives the process by years; that is the point of it, and the reason it
        // comes last.
        //
        // Nothing is deleted. There is deliberately no retained-file limit and no size cap: a cap is a number nobody chose
        // that silently destroys the record you go looking for. Files roll by DAY so they stay readable, and pruning them is
        // an operator's decision made with a broom, not a policy this app invents. Set Logging:FilePath to move them.
        //
        // The LEVEL is Serilog's own, read from the same Logging:LogLevel keys the rest of the app uses. The Logging:LogLevel
        // filter does NOT apply to this provider, so it sets the level itself rather than defaulting to Verbose: a Verbose
        // file sink filters nothing and firehoses Verbose/Debug ASP.NET internals onto disk — filling it (there is
        // deliberately no cap to stop it) and putting request paths on disk at Debug. One source of truth, honoured here.
        // The path is a machine setting, not an appsettings.json key. The literal here is the shipped default, kept in code
        // so an install that has never set the path still logs — to this default location — rather than silently stopping.
        // Blank the setting to turn the file sink off.
        string logFilePath = config[ConfigKeys.LoggingFilePath] ?? "logs/imagegen-.log";
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            string logPath = Path.IsPathRooted(logFilePath) ? logFilePath : Path.Combine(builder.Environment.ContentRootPath, logFilePath);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? throw new InvalidOperationException($"Log path '{logPath}' has no parent directory."));

            // Unset keeps the shipped default level; a SET-but-unparseable value is a typo and throws rather than silently
            // applying a different level than the operator asked for. None parses but has no Serilog equivalent, so the file
            // sink keeps its own default there.
            static Serilog.Events.LogEventLevel Level(string? configured, Serilog.Events.LogEventLevel fallback)
            {
                if (string.IsNullOrWhiteSpace(configured))
                {
                    return fallback;
                }

                if (!Enum.TryParse(configured, ignoreCase: true, out LogLevel mel))
                {
                    throw new InvalidOperationException(
                        $"A configured log level is '{configured}', which is not one of Trace, Debug, Information, Warning, Error, Critical, None.");
                }

                return mel == LogLevel.None ? fallback : (Serilog.Events.LogEventLevel)mel;   // MEL Trace..Critical map 1:1 onto Serilog Verbose..Fatal
            }

            _ = builder.Logging.AddSerilog(new LoggerConfiguration()
                .MinimumLevel.Is(Level(config[ConfigKeys.LogLevelDefault], Serilog.Events.LogEventLevel.Information))
                .MinimumLevel.Override(LogNames.MicrosoftSource, Level(config[ConfigKeys.LogLevelMicrosoftAspNetCore], Serilog.Events.LogEventLevel.Warning))
                .WriteTo.File(
                    logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: null,   // keep every day. See above: no invented cap.
                    fileSizeLimitBytes: null,
                    shared: true,
                    outputTemplate: LogNames.OutputTemplate)
                .CreateLogger(), dispose: true);
        }

        // Options, bound from config into the internal option objects.
        //
        // The keys say what they configure: ComfyUI:* is the render backend, Catalog:* is the workflow catalogue, Media:* is
        // ffmpeg -- named for what they configure, so "which of these is the ComfyUI address" needs no history to answer.
        // There is no compatibility read of any other spelling: a key that is silently accepted under two names is a key
        // nobody can ever remove.
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
        // The catalogue ships WITH the application: the release archive copies this directory next to the binary.
        const string shippedCatalogDir = "configurations";
        ComfyOptions comfyOptions = new()
        {
            CatalogPath = shippedCatalogDir,
        };
        // ffmpeg runs IN-PROCESS (Loxifi.FFmpeg), so there is no executable to locate, no download step, and no path to
        // configure. The defaults pick Cisco's OpenH264 from the LGPL runtime -- see MediaOptions for why x264 is not the
        // default despite being the better encoder.
        MediaOptions mediaOptions = new();
        // The foreground-idle delay before background (idle-time) jobs run. Read LIVE on every scheduling decision (the
        // settings page can change it while the app runs), exactly like the memory floor below — so it is a delegate over
        // IConfiguration, not a captured value. Only the STORED value is live; the fallback is the constant declared on the
        // setting's spec (so the settings page and this reader cannot disagree about an unset box), resolved once here.
        double backgroundIdleDefault = double.Parse(
            MachineSettingSpecs.DefaultOf(MachineSettingSpecs.Keys.BackgroundIdleMinutes)
                ?? throw new InvalidOperationException($"{MachineSettingSpecs.Keys.BackgroundIdleMinutes} declares no default."),
            System.Globalization.CultureInfo.InvariantCulture);
        RenderOptions renderOptions = new(() =>
            TimeSpan.FromMinutes(config.GetValue(MachineSettingSpecs.Keys.BackgroundIdleMinutes, backgroundIdleDefault)));

        // --- DI (composition root) -----------------------------------------------------------------------
        _ = builder.Services.AddSingleton<AuthOptions>();          // reads Auth:RegistrationCode live
        _ = builder.Services.AddSingleton(TimeProvider.System);

        // The machine-settings source, and the service that reads and writes it. The source is registered as itself so the
        // writer can reach the built provider; MachineConfigService is what the API and the setup flow talk to.
        _ = builder.Services.AddSingleton(machineSettings);
        _ = builder.Services.AddSingleton<MachineConfigService>();
        _ = builder.Services.AddSingleton<ComfyProbe>();   // "did anything answer at that address" — setup page and settings API

        // The patches page. ComfyUI's INSTALLATION rather than its address: the app has only ever known the renderer as a
        // URL, and a URL cannot say which directory this process may write to. All four read their configuration live, so
        // pointing the box at a different ComfyUI needs no restart. PackSource/PatchInstaller are the engine the container
        // build runs too (tools/ComfyPatch) — one implementation, so the build and the page cannot disagree about state.
        _ = builder.Services.AddSingleton<ComfyInstall>();
        _ = builder.Services.AddSingleton<ComfySupervisor>();
        _ = builder.Services.AddSingleton<PackSource>();
        _ = builder.Services.AddSingleton<PatchInstaller>();
        _ = builder.Services.AddSingleton<ComfyPatchService>();

        // "Is there a newer release?" — cached and re-asked when a request finds the answer over an hour old (see
        // UpdateCheck). Singleton so the one cached answer is shared across every request, not re-fetched per call.
        _ = builder.Services.AddSingleton(sp => new UpdateCheck(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<UpdateCheck>>(),
            AppVersion.Current));

        // LoRA trigger-word / preview lookup against CivitAI by file hash. Opt-out (Civitai:Enabled), like the update check.
        _ = builder.Services.AddSingleton<ImageGen.Application.Civitai.ICivitaiClient, ImageGen.Web.Civitai.CivitaiClient>();

        // Only the composition root knows what a configuration key is called, so the live endpoint lives here rather than
        // in the Comfy adapter. Nothing is cached: a read is a dictionary lookup against the current configuration.
        _ = builder.Services.AddSingleton<IComfyEndpoint>(sp => new ConfiguredComfyEndpoint(sp.GetRequiredService<IConfiguration>()));

        _ = builder.Services.AddInfrastructure(connectionString, databaseProvider);
        _ = builder.Services.AddApplication(renderOptions, config.GetValue(ConfigKeys.AuditUserPrompts, false));

        // Admission control for work that will hold memory until it renders. Uploaded render inputs stay resident until their
        // job runs and are never evicted (see IUploadStore), so a box below this floor refuses new submissions outright and
        // tells the caller — the alternative, dropping inputs to make room, destroys work that was already accepted.
        // Per-OS, because there is no managed way to ask "how much memory does this BOX have left" -- GC.GetGCMemoryInfo
        // describes this process's heap, which is a different question. An unsupported OS throws at startup rather than
        // guessing: this gate decides whether to accept work that will hold memory until it renders.
        _ = builder.Services.AddSingleton<ISystemMemory>(_ =>
            OperatingSystem.IsWindows() ? new WindowsSystemMemory()
            : OperatingSystem.IsLinux() ? new LinuxSystemMemory()
            : throw new PlatformNotSupportedException(
                $"no available-memory probe for {Environment.OSVersion.Platform}. Windows and Linux are implemented "
                + "(see ISystemMemory); the submission gate cannot run without one."));
        // The floor is read per check, not captured, so raising it on the settings page takes effect on the next submission.
        _ = builder.Services.AddSingleton(sp => new SubmissionMemoryGate(
            sp.GetRequiredService<ISystemMemory>(),
            () => sp.GetRequiredService<IConfiguration>().GetValue(ConfigKeys.MinAvailableMemoryMB, 500L) * 1024L * 1024L));
        _ = builder.Services.AddComfy(comfyOptions);
        _ = builder.Services.AddMedia(mediaOptions);

        // The tag model, in-process. Serves BOTH tag ports -- '#'/'@' autocomplete and the random-artist pick (ITagCatalog),
        // and context-ranked suggestions plus whole-prompt generation (ITagModelClient) -- from one loaded model. This replaces
        // tags.json AND the separate Python service on port 8000; there is no Tags:ModelUrl any more because there is no URL.
        // Loads at startup on purpose: a missing artifact breaks autocomplete and fails every random-prompt render, so
        // refusing to start with the missing filename named beats serving a page whose tag box quietly does nothing.
        //
        // The app FETCHES the artifacts itself if they are not there, so there is no separate download step a user has to
        // know to run (or know not to run) before anything works: the app knows it needs the file, so the app gets it.
        // Shared cache vs pre-cache: writing the machine-wide shared cache (%LOCALAPPDATA%) is OPT-IN (default off), so
        // by default a build stages its artifacts in its own install folder (the pre-cache) and never touches the shared
        // copy other installs read. Flip TagModel:WriteToSharedCache (appsettings) or TagModel__WriteToSharedCache (env,
        // to turn it on machine-wide) to use the shared cache. The download and the load MUST target the same directory.
        bool writeToSharedCache = builder.Configuration.GetValue(ConfigKeys.TagModelWriteToSharedCache, false);
        string tagModelArtifacts = TagModelServiceCollectionExtensions.ArtifactsDirectory(writeToSharedCache);
        using ILoggerFactory startupLoggers = LoggerFactory.Create(b => b.AddConsole());
        using (HttpClient artifactsHttp = new()
        { Timeout = Timeout.InfiniteTimeSpan })   // ~900 MB on a first run
        {
            await TagModelArtifacts.EnsureAsync(
                artifactsHttp, startupLoggers.CreateLogger(LogNames.TagModelCategory), tagModelArtifacts, CancellationToken.None);
        }

        _ = builder.Services.AddTagModel(tagModelArtifacts);
        _ = builder.Services.AddMemoryCache();   // backs the /forge/image?w=N thumbnail + mp4 caches

        // The minimal-API endpoints that bind their body directly (e.g. /generate, /edit, /enqueue) deserialize through these
        // options, NOT Json.Options. Match the two Respect* flags so both paths enforce the wire DTOs' required/non-nullable
        // annotations identically — a missing/null required member is a clean 400 at the boundary on either route. (Controllers
        // use their own MvcJsonOptions and are unaffected.)
        _ = builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.RespectRequiredConstructorParameters = true;
            o.SerializerOptions.RespectNullableAnnotations = true;
        });

        _ = builder.Services.AddControllersWithViews();

        // Run the render orchestrator's background loop (the core orchestrator is a plain singleton; this adapts it to a
        // hosted service). Registered here, alongside the singleton the AddApplication call created.
        _ = builder.Services.AddHostedService<RenderWorker>();

        // Run the single snapshot sync worker's serial rebuild loop (warms every registered source on boot, then
        // rebuilds on invalidation/backstop). Same plain-singleton-adapted-to-hosted-service split as RenderWorker.
        _ = builder.Services.AddHostedService<SnapshotSyncService>();

        // Vestigial reconciler: reaps stale PendingJob rows (history is worker-written). Toggle off via Reconciler:Enabled.
        if (config.IsOn(ConfigKeys.ReconcilerEnabled))
        {
            _ = builder.Services.AddHostedService<PendingJobReconciler>();
        }

        // /forge/upload reads the posted file via ReadFormAsync, whose 128MB default would become the new
        // binding limit once Kestrel's MaxRequestBodySize is raised past it. Keep it in step with the Kestrel
        // limit (appsettings.json) and nginx's client_max_body_size (deploy/imagegen-nginx.conf).
        _ = builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = 536870912;
        });

        // Clearing the known-proxy lists makes the app honour X-Forwarded-* from ANY caller, which is what a single box behind
        // its own trusted reverse proxy wants and how this has always run. It is also spoofable by anyone who can reach the app
        // directly, so a packaged install that is exposed differently needs to be able to say no. Defaults to the historical
        // behaviour; set Security:TrustAllProxies=false to keep ASP.NET's loopback-only default instead.
        bool trustAllProxies = config.IsOn(ConfigKeys.TrustAllProxies);
        _ = builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            if (trustAllProxies)
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            }
        });

        _ = builder.Services
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
                    if (ctx.Request.Path.StartsWithSegments(Routes.ApiPrefix))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    ctx.Response.Redirect(ctx.RedirectUri);
                    return Task.CompletedTask;
                };
                // A valid signature is not a valid session. The identity does NOT live in the cookie — the cookie carries an
                // opaque session key and DbTicketStore (wired in as SessionStore just below) holds the ticket in dbo.AuthSession.
                // So a cookie is a handle to a session, not a self-contained "I am user 1" assertion that keeps meaning that
                // for as long as its signature verifies. That is what closes the ghost-cookie hole: wiping the database wipes
                // the session rows too, so a surviving cookie names no session and the request is simply anonymous — and
                // checking "does a user with this id still exist" would not have saved a self-contained cookie, because ids are
                // BIGINT IDENTITY and a re-created first account retakes id 1, so a ghost would authenticate as whoever now
                // holds its id. Being in the database (not in-process, as it used to be), a session survives an app restart.
            });

        // Auth persistence lives in the database, both halves of it: the Data Protection key ring (dbo.DataProtectionKey —
        // the keys that sign the session cookie move with the database instead of sitting in the OS user profile) and the
        // server-side session state for the cookie above (dbo.AuthSession, via DbTicketStore). The store is post-configured
        // onto the cookie options here, after DI is built, because the AddCookie callback runs before the provider exists.
        _ = builder.Services.AddDataProtection();
        _ = builder.Services
            .AddOptions<KeyManagementOptions>()
            .Configure<IDataProtectionKeyRepository>((options, keys) => options.XmlRepository = new DbXmlRepository(keys));
        _ = builder.Services.AddSingleton<DbTicketStore>();
        _ = builder.Services
            .AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<DbTicketStore>((options, store) => options.SessionStore = store);

        _ = builder.Services.AddAuthorization();

        // Move off a port something else already holds, rather than refusing to start. Kestrel's own answer to a taken
        // port is to throw, and on a desktop box that almost always means the app is already running or something grabbed
        // 8080 -- a failure the user can do nothing with at the moment they hit it. The next port up is used instead and
        // said out loud, here and in Kestrel's own "Now listening on" line.
        //
        // NOTE for a proxied deployment: nginx forwards to a fixed port, so an app that quietly moved is an app the proxy
        // can no longer reach. The warning is the only signal; pin the port and keep it free if that matters.
        string? configuredUrls = config[ConfigKeys.Urls];
        string? listenUrls = ListenAddress.Resolve(
            configuredUrls,
            onMoved: (host, wanted, actual) => startupLoggers.CreateLogger(LogNames.StartupCategory).LogWarning(
                "Port {Wanted} on {Host} is already in use; listening on {Actual} instead.", wanted, host, actual));
        if (listenUrls != configuredUrls)
        {
            config[ConfigKeys.Urls] = listenUrls;
        }

        WebApplication app = builder.Build();

        // (The schema is applied at the top of this file, before the machine settings are read out of it.)

        // Warm the booru tag store at startup (it loads its large file once in the background, not on the first /forge/tags hit).
        _ = app.Services.GetRequiredService<ITagCatalog>();

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
        _ = app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
        {
            bool exposeStackTraces = app.Configuration.IsOn(ConfigKeys.ExposeStackTraces);
            Exception? ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = (exposeStackTraces ? ex?.ToString() : ex?.Message)
                    ?? "Unhandled server error (no exception captured).",
                type = exposeStackTraces ? ex?.GetType().FullName : null,
            });
        }));

        _ = app.UseForwardedHeaders();
        _ = app.UseStaticFiles();
        // Before auth, because a box that has never been configured has no accounts to authenticate against — the setting
        // that gates registration is one of the ones being asked for. Stops matching as soon as they are set.
        _ = app.UseMiddleware<SetupRequiredMiddleware>();
        _ = app.UseWebSockets();   // for /forge/ws (live progress)
        _ = app.UseAuthentication();
        // After the cookie handler, before authorization: a per-user API key (AppUser.ApiKey, sent as X-Api-Key or
        // Authorization: Bearer) stands in for the login cookie, so API apps can act as a specific user. No-op for browsers.
        _ = app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
        _ = app.UseAuthorization();

        _ = app.MapControllers();
        app.MapImageGenApi();   // /api (client actions) + /forge (render backend)

        // Ops/deploy drain probe (anonymous, root — like a health check). Reports how much render work is in flight so a
        // deploy can wait it out before stopping the app. Slots merely waiting re-hydrate and resume, so the gate keys
        // on work in flight, not queue depth. Exposes only counts — no prompts, no user data.
        _ = app.MapGet(Routes.DrainStatus, (RenderOrchestrator queue) =>
        {
            WorkloadSnapshot w = queue.Workload();
            return (Task)Results.Ok(new
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
        foreach (string address in app.Urls)
        {
            string reachable = StartupBrowser.Reachable(address);
            firstReachable ??= reachable;
            Console.WriteLine();
            // ASCII only. This is the one line a user is told to read, and a Windows console on a non-UTF-8 code page
            // turns anything else into mojibake.
            Console.WriteLine($"  ImageGen is running - open {reachable}");
            Console.WriteLine();
        }

        // And open it, if the launcher asked. Only the launcher sets this, so a container, a service or a scheduled task
        // is left alone — see StartupBrowser for why that is the trigger rather than a guess at whether a desktop exists.
        if (firstReachable is not null && StartupBrowser.Requested(Environment.GetEnvironmentVariable(StartupBrowser.Env.EnvVar)))
        {
            StartupBrowser.Open(firstReachable, startupLoggers.CreateLogger(LogNames.StartupCategory));
        }

        await app.WaitForShutdownAsync();
    }
}
