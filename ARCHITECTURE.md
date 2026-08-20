# ImageGen — Architecture

This is the canonical description of how ImageGen is built: the components, how they interact, what
state lives where, and the invariants every change must preserve. It exists because the system has
started to spaghettify — state ending up in the wrong place (e.g. composer/gen state in browser
`localStorage` instead of the per-user database), and a per-process design quietly assumed by code that
is deployed as multiple processes. **When code and this document disagree, that is a bug in one of
them — fix it, don't widen the gap.** The last section lists the gaps known at time of writing.

Related docs: `INSTALL.md` (how to install and configure it), `README.md` (overview),
`docs/DESIGN_PHILOSOPHY.md` (the visual language new screens are checked against). This doc is the authority on
*design*.

> **"Forge" is a name that outlived its project.** `ImageGen.Forge` — the fat "everything" library — was
> **dissolved** into the core plus adapters: `ImageGen.Comfy` (ComfyUI client, workflow engine, catalog),
> `ImageGen.TagModel` (in-process ONNX), `ImageGen.Media` (ImageSharp + in-process ffmpeg) and `ImageGen.Api` (JSON
> endpoints). `JobQueue` became `ImageGen.Application.Rendering.RenderOrchestrator`. §3 has the real graph.
>
> **The config keys have since been renamed** to say what they configure: `ComfyUI:BaseUrl` / `ComfyUI:GateToken`,
> `Catalog:WorkflowsPath` / `Catalog:RequirementsPath`. There is **no compatibility read of the
> old `Forge:*` spelling** — a key accepted under two names is a key nobody can ever remove — so a deployment
> carrying an `appsettings.Production.json` written against the old names must be migrated **in the same step as
> the deploy**, or it silently falls back to defaults.
>
> The **`/forge` route prefix stays.** It is a wire contract the external MCP and the browser both call, and
> renaming it buys nothing a caller can see. Where later sections say "Forge" of a *type*, read the adapter that
> replaced it.

---

## 1. What it is

An ASP.NET Core (`ImageGen.slnx`, .NET, onion architecture) app that:

- Serves a server-rendered MVC UI (Razor + vanilla JS, no SPA framework) for generating, editing,
  browsing, and bookmarking images.
- Exposes a per-user history/bookmark/settings API (`/api/*`) backed by SQLite or SQL Server.
- **Embeds the image-generation backend in-process** — `RenderOrchestrator` (Application) driving the
  `ImageGen.Comfy` adapter against a ComfyUI instance. Those **classes are the in-process interface**;
  `/forge` is their HTTP projection for **out-of-process callers only** — the browser UI (same-origin) and
  the external MCP (the reason `/forge` is exposed publicly). The app's own server-side code never calls
  `/forge` over HTTP; it uses the classes directly. (This was a standalone "ForgeGateway" service; it is now
  in-process — one deployable, no loopback.)
- Authenticates with **local username/password accounts** (cookie sessions). No external IdP.

There is one logical product, multiple users, and a shared database.

---

## 2. Deployment topology

```
   ┌─────────────────────────────────────────────────────────┐
   │  ImageGen app  :8080  (Kestrel)                          │
   │    ├─ /api    per-user history / bookmarks / settings     │
   │    ├─ /forge  → ComfyUI  :8188                            │
   │    └─ tag model IN-PROCESS (ONNX Runtime)                 │
   │                                                           │
   │  Database:  a SQLite file, or SQL Server                  │
   │             single source of truth for ALL user state     │
   └─────────────────────────────────────────────────────────┘
                              │
                              ▼
                    ComfyUI  :8188   (GPU; --enable-cors-header)
```

**One process holds everything the app needs.** Two services used to sit beside it and both were folded in: the
`ForgeGateway` (now `/forge`, same origin) and the Python tag model (now in-process via ONNX Runtime — there is no
`Tags:ModelUrl`, because there is no URL). What remains outside is ComfyUI, which owns the GPU, and the database.

**The database is the single shared source of truth.** Everything durable and user-meaningful lives there — accounts,
history, bookmarks, jobs, and the image bytes themselves. It is the *only* sanctioned channel for cross-device and
cross-instance state. (See §8 for what is *not* in it, and why that is the central fault line.)

**How many app instances may share it depends on the provider**, and this is the one topology constraint that is not a
preference:

| Provider | Instances | Why |
| --- | --- | --- |
| **SQLite** | exactly **one** | SQLite permits a single writer. This is a write-heavy render orchestrator — the queue writes through on every slot transition — so a second instance against the same file is not slow, it is wrong. |
| **SQL Server** | several | Concurrent writers are supported. Each instance drives its own ComfyUI and owns its own jobs (`dbo.Job.MachineName`), sharing only the database. |

Per-machine configuration lives in `dbo.MachineSetting`, keyed by machine name, and is edited under
**Settings → This machine**. The appsettings file holds only what has to be known before the app can read that
table: the connection string, the provider, the listen address and the request-size limit. A packaged install is
asked for the renderer's address on first run; everything else has a working default. See `INSTALL.md`.

> Anything about a *specific* deployment — a reverse proxy, TLS, hostnames, remote access, which box runs what — is
> deliberately not in this repository. It is the operator's own business, and the author's own arrangements live in a
> gitignored `local/` (redeploy + service scripts, schema/database setup, the nginx conf, per-machine configs).
> **`local/` is gitignored and therefore not backed up by git** — if the working tree goes, it goes with it.

---

## 3. Solution structure & layering (onion)

```
                    Domain ← Application ← Infrastructure
                               ↑ ↑ ↑ ↑
                  Comfy ───────┘ │ │ └────── Media        (adapters implement Application ports;
                  TagModel ──────┘ └──────── Api           no adapter references another adapter)

ImageGen.Web (host) → Application, Infrastructure, Comfy, TagModel, Media, Api
```

| Project | Layer | Responsibility | May depend on |
|---|---|---|---|
| **ImageGen.Domain** | Domain | Entities, repository *interfaces*, `TokenKind`; `IUserLogService` | nothing |
| **ImageGen.Application** | Application | Services (use-cases): `UserService`, `HistoryService`, `BookmarkService`, `BanService`, `ArtistService`; `PasswordHasher`; **`UserCrypto` + `IUserCipher`** (per-user column cipher), `UserLogService` | Domain |
| **ImageGen.Infrastructure** | Infrastructure | ADO.NET repositories, `IDbConnectionFactory`, `DatabaseInitializer`, `schema.sql`; **`UserCipher`** (key load/cache), `UserLogRepository` | Application, Domain |
| **ImageGen.Comfy** | Backend adapter | `ComfyClient`, `WorkflowCatalog`, `WorkflowRegistry` + the per-model `IWorkflow` classes; **`Patches/`** — the patch engine over a ComfyUI *installation* (§7.2.1) | Application, Domain |
| **ImageGen.TagModel** | Backend adapter | The tag model in-process: ONNX session, vocabulary, suggest + generate engines. Serves BOTH `ITagCatalog` and `ITagModelClient` | Application |
| **ImageGen.Media** | Backend adapter | ImageSharp + ffmpeg IN-PROCESS via Loxifi.FFmpeg (`WebpTranscoder`, `MediaProcessor`) | Application |
| **ImageGen.Api** | Presentation | Every JSON endpoint: `/api` groups and `/forge` | Application, ASP.NET |
| **ImageGen.Web** | Presentation/host | Composition root (`Program.cs`), MVC controllers, Razor views, `wwwroot` JS | Application, Infrastructure, and every adapter |

**Dependency rule:** dependencies point inward toward Domain. Domain references nothing, and the core
(Domain + Application) contains **zero ComfyUI, ASP.NET or ImageSharp types** — each of those lives behind a
port (`IComfyClient`, `IWorkflowCatalog`, `ITagCatalog`, `ITagModelClient`, `IMediaProcessor`, `IUserCipher`)
declared in Application and implemented by exactly one adapter. No adapter references another adapter; the
host is the only project that sees them all, which is what keeps the wiring in one place (§4).

---

## 4. Composition root & DI lifetimes

All wiring is in `src/ImageGen.Web/Program.cs` plus `ForgeServiceCollectionExtensions.AddForge()`.

- **Singletons (stateless):** `IDbConnectionFactory` → `SqlConnectionFactory`, `TimeProvider`,
  `AuthOptions`, `ForgeConfig`, `WorkflowCatalog`, `WorkflowRegistry`, and every `IWorkflow` (one per model,
  registered explicitly in `AddWorkflows()`). The workflows are pure graph builders (no mutable state).
- **Scoped (per request):** every repository (`IUserRepository`, `IHistoryRepository`,
  `IBookmarkRepository`, `IBannedTokenRepository`, `IArtistDisplayRepository`)
  and every application service.
- **Singletons that hold mutable in-memory state — read this twice:**
  - `JobQueue` (also an `IHostedService` worker) — the live job index, per-user queues, fairness
    state, and the ComfyUI-prompt-id↔jobId map. **Process-local. Not shared across instances.**
  - `TagModelBundle` / `VocabTagCatalog` — the tag model's ONNX session and its 639k-tag vocabulary, loaded once at
    startup. Immutable after load, and read-only per request; the ~900 MB of weights is why it is loaded exactly once.
    (This was `TagStore` over a `tags.json` file, which no longer exists — see §6.)
    > **Parity, not vibes.** `tests/ImageGen.Tests/tagmodel-parity.json` is a recording of the *real Python
    > server's* answers, captured while it still ran; it pins suggest ordering, calibrated probabilities and
    > greedy generation exactly. It already caught a real bug: an empty context must **not** run a forward
    > pass — Python ranks by corpus base rate there, which is why `1girl` leads an empty prompt box. **Replace
    > the checkpoint and this snapshot is stale**, and must be re-captured against something that can still
    > answer. `TagModelParityTests` *skips itself* when the artifacts are absent — a skip is not a pass.
  - `IMemoryCache` — thumbnail JPEG cache for `/forge/image?w=N`.
  - `ComfyClient` — holds one fixed ComfyUI `client_id` minted per process.
  - `IImageBlobRepository` → `ImageBlobRepository` **and** `IJobRepository` → `JobRepository` (and
    `IGenTimingRepository`, **`IUserLogRepository` → `UserLogRepository`**, **`IUserLogService` → `UserLogService`**)
    are registered **Singleton** (not Scoped like the other repos) specifically because the singleton `JobQueue`
    resolves them to persist images, write jobs through, record timings, and write the per-user audit log.
    They're stateless (a fresh connection per call) so this is safe — a deliberate exception to "repositories
    are scoped."
  - **`IUserCipher` → `UserCipher` (Singleton).** The per-user column cipher (§6.1). Holds a
    `ConcurrentDictionary` cache of each user's derived subkeys, so it's a *stateful* singleton.
    It must be a singleton because the singleton `JobRepository` depends on it; the Scoped repos depend on it too
    (a Scoped service may depend on a Singleton — the reverse is the forbidden direction).

> **Canonical rule:** a Singleton may not depend on a Scoped service. `JobQueue` (singleton) persisting
> images/jobs is why `ImageBlobRepository`/`JobRepository` are singletons. Any new durable write from
> `JobQueue` must go through a similarly singleton-safe path (open its own scope, or use a singleton-stateless
> repo).

Background services: **`RenderWorker`** (adapts the orchestrator's render loop) and **`SnapshotSyncService`**
(serial snapshot warming/rebuild). Both start with the host.

---

## 5. Authentication & request ownership

- **Scheme:** cookie auth (`imagegen_auth`, HttpOnly, SameSite=Lax, 30-day sliding). Login/register/
  logout in `AccountController`; passwords hashed by `PasswordHasher` (PBKDF2). New sign-ups require a
  shared `Auth:RegistrationCode` (the app is internet-reachable). **No OAuth/Google/broker** — each
  instance authenticates directly against the shared DB and sets its own cookie.
- **Identity:** user id is the `NameIdentifier` claim; read via `ClaimsPrincipal.GetUserId() → long?`.
- **`/api/*`** is a single `MapGroup("/api").RequireAuthorization()` — every endpoint group hangs off
  it (`History`, `Bookmark`, `Ban`, `Import`, `Pending`, `Artist`, `Settings`). 401 (not redirect) is
  returned for unauthenticated `/api` calls.
- **`/forge/*` ownership:** a `/forge` endpoint filter resolves the owning user **once per request** and
  stashes it in `HttpContext.Items["ForgeOwnerUserId"]`; endpoints read it via `OwnerOf(http)`. Two ways
  to authenticate `/forge`:
  1. **Browser cookie** → owner = the logged-in user.
  2. **A per-user API key** (for non-browser callers, i.e. the MCP), sent as `X-Api-Key` or
     `Authorization: Bearer` → owner = the user that key belongs to. `ApiKeyAuthenticationMiddleware`
     resolves it against `AppUser.ApiKey` in the database and builds a principal shaped exactly like
     the login cookie's, so every downstream ownership check is identical. An unknown or blank key
     leaves the request anonymous and authorization rejects it.

  > There is **no app-wide API key**. The old `Forge:ApiKey` / `Forge:ApiKeyUserId` pair — one shared
  > secret mapped to one hardcoded owner — was replaced by per-user keys; nothing reads those config
  > keys any more. See §6 on per-user scoping.

> **Canonical rule:** every job, image, history row, bookmark, ban, and setting belongs to exactly one
> user id, resolved server-side from the authenticated principal. The client never asserts its own
> identity or ownership.

> **Authenticated-user control boundary:** this is a small, cooperatively administered instance with no roles;
> every authenticated user is trusted to control machine-wide render work. Consequently `/forge/cancel/{id}`,
> `/forge/interrupt`, and `/forge/cancel-all` deliberately allow any signed-in user to stop any job. That operator
> trust does **not** cross the data-privacy boundary or mint work as somebody else: image/job reads remain owner-checked,
> edit inputs must be readable by the submitting user, and requeue creates work only for the original owner.

---

## 6. Data model — the source of truth

SQL Server, schema in `src/ImageGen.Infrastructure/Database/schema.sql` (idempotent: every object is
`IF NOT EXISTS`/additive; safe to re-run; **never** drops data). The app login `imagegen_app` has
datareader/datawriter only — **no DDL** — so schema changes are applied out-of-band with an elevated
`sqlcmd -E`. Under SQLite there is no login and no elevation, so `Database:EnsureSchemaOnStartup` applies
`schema.sqlite.sql` at startup instead — see `INSTALL.md`.

| Table | Owner key | Holds | Notes |
|---|---|---|---|
| `AppUser` | `Id` | account + **per-user settings**: `ComposerPrefs` (opaque JSON composer state, incl. the random-prompt temperature) | settings live here so they follow the user across devices |
| `HistoryEntry` (+ `HistoryMark`) | `UserId` | a generated image in the user's library: `GatewayImageId`, prompt, model, aspect, marks | **unique `(UserId, GatewayImageId)`** → all history writes dedupe |
| `ImageBookmark` (+ `ImageBookmarkMark`) | `UserId` | starred images (self-contained copy; survives history deletion) | unique `(UserId, GatewayImageId)` |
| `TokenBookmark` | `UserId` | starred tags/artists | unique `(UserId, Name, Kind)` |
| `BannedToken` | `UserId, ModelId` | per-model banned tags/artists (excluded from **auto-gen only**) | unique `(UserId, ModelId, Name, Kind)` |
| `ArtistDisplay` | `UserId, ArtistName` | the chosen display image for an artist | falls back to latest gen if unset |
| `Job` (+ `JobSlot`) | `JobId` (GUID) | the **authoritative job lifecycle**: owning `MachineName`, `Total`/`Status`, and one `JobSlot` per image (state, `ComfyPromptId`, produced `ImageId`, effective prompt, marks, request payload) | write-through cache target of `JobQueue`; finalized rows readable by id from any instance |
| `ImageBlob` | `ImageId` (GUID) | **the actual image bytes** of a *generated* image, width/height, content-type | global GUID id, never a ComfyUI filename; **uploads never land here** (see below) |
| `UserEncryptionKey` | `UserId` (1:1 `AppUser`) | the user's random 32-byte master key (`KeyMaterial`) | **its own table on purpose** (§6.1) so routine queries never pull key material; obvious name = "don't `SELECT` this" |
| `UserLog` | `UserId` | per-user **encrypted** audit log: `Category` + `Payload` (ciphertext) of prompt-bearing events | opt-in (`Logging:AuditUserPrompts`); the private alternative to plaintext app logs (§6.1) |

`TokenKind`: 0 = Tag, 1 = Artist. Marks (`{ token → tag|artist }`) are the bookmarkable tokens of a
prompt and are stored as child rows.

The prompt/tag-bearing columns of the tables above are **encrypted at rest per-user** (§6.1): prompts as
randomized ciphertext, searchable tokens as deterministic ciphertext. Reads/writes go through the repositories,
which decrypt/encrypt transparently — the rest of the system sees plaintext.

**Image storage** is DB-first: `ImageBlob` is the durable home; `/forge/image/{id}` serves from the DB
and only falls back to ComfyUI `/view` for legacy ids minted before DB storage. This solved
filename-collision and output-dir-rotation problems from the app and MCP sharing one ComfyUI.

**Uploads are never persisted.** An uploaded edit source, reference image, inpaint mask or i2v end frame is a
render *input*: it never enters history, the library or a bookmark, so nothing can retrieve one afterwards.
`POST /forge/upload` first passes the live `SubmissionMemoryGate` check against
`Uploads:MinAvailableMemoryMB`, then puts the bytes in the process-local `IUploadStore` (`Application/Images`,
singleton, never evicted while the process runs); every read path checks it before `ImageBlob`. They used to be written as
`ImageBlob` rows with `Kind=1`, one per inpaint stroke — 19,329 rows / 7.1 GB of write-only data, whose only
reference lived *inside the encrypted* `JobSlot.RequestJson`, so nothing could tell which were still in use. That
blob is gone: a slot's spec is typed columns now, and its image ids are plain, joinable columns plus a
`JobSlotReference` child table — a foreign key inside an encrypted blob was never a foreign key.
The `Kind` column survives as history; nothing writes `Upload` any more. Trade-off: an edit slot that is
queued but not yet submitted does not survive a restart, because its source died with the process.

**Deleting an image is a cascade, not a history delete.** `IImageDeletionRepository.DeleteEverywhereAsync`
removes, in one transaction: the `HistoryEntry` (+`HistoryMark`), the `ImageBookmark` (+ its marks and
categories), any `ArtistDisplay` using it, its `ImageFrame` rows, the producing `JobSlot` (only when that job
is **finalized** — a live job re-upserts its whole slot set, so its slots are swept at finalization instead),
the `Job` if that emptied it, and finally the `ImageBlob` itself. `IHistoryRepository` deliberately exposes no
delete: deleting only the history row is exactly what stranded 713 blobs and would strand every other
reference.

Everything in this section is **shared across instances** because it's in the one DB. That is the design.
The next two sections are where it stops being shared.

### 6.1 At-rest encryption & the privacy model

The threat model is **accidental viewing**, not a determined attacker: anything potentially embarrassing
(prompts, the tags/artists a generation hit) should take *deliberate work* to read, not appear the moment
someone opens a table in SSMS or skims a log. Keys live in the same database — that is accepted. The bar is
"you'd have to consciously join the key table and run the app's exact AES routine," nothing stronger.

- **Per-user keys, no master key.** Each user has a random 32-byte key in **`dbo.UserEncryptionKey`** — its
  *own* table, not a column on `AppUser`, so ordinary queries over user/history/bookmark data never ingest key
  material, and the table name flags it as off-limits. The key is provisioned lazily on first use (race-safe
  insert) and cached. No master key = no single point of total data loss; lose a row and only that user is lost.
- **The cipher (`UserCrypto` / `IUserCipher` → `UserCipher`).** Pure BCL (`AesGcm`/`HKDF`/`HMACSHA256`). The
  master key is stretched (HKDF) into three subkeys. Two modes:
  - **Randomized** (`enc:v1:`, fresh nonce) for free-text never searched by value: `HistoryEntry.Prompt`,
    `ImageBookmark.Prompt`, `Job.Prompt`, `JobSlot.{EffectivePrompt,Prompt,NegativePrompt}`,
    `AppUser.ComposerPrefs`, and `UserLog.Payload`.
  - **Deterministic** (`det:v1:`, synthetic nonce = `HMAC(plaintext)`) for searchable tokens that must keep
    equality filters and UNIQUE constraints working: `HistoryMark.Token`, `ImageBookmarkMark.Token`,
    `TokenBookmark.Name`, `BannedToken.Name`, `ArtistDisplay.ArtistName`. Same plaintext+user → same ciphertext,
    so `WHERE Token = @x`, `IN (...)`, `PARTITION BY`, and UNIQUE all still work — the repos just encrypt the
    filter value too. (These token columns were widened `NVARCHAR(256)→512` to fit the longer ciphertext.)
  - **Accepted trade-off:** deterministic columns leak *equality* (which rows share a still-secret token —
    frequency-analyzable). That is the unavoidable cost of keeping tags searchable without server-side decryption;
    prompts (randomized) don't leak it. Documented in §10.3.
- **The repositories are the single encrypt/decrypt boundary.** They encrypt on write and decrypt on read; the
  worker, services, and UI only ever see plaintext. Decrypt is **tolerant** — a value without an `enc:`/`det:`
  prefix is returned verbatim — so legacy plaintext rows read fine and migration is gradual/idempotent.
- **Backfilling existing rows is a standalone, throwaway EXE** (`tools/ImageGen.EncryptionBackfill`), never part
  of the app — run once against the DB, then deleted. It reuses the real cipher so its ciphertext matches.

**Prompt logging.** Prompts used to leak two ways: `ComfyClient` logged the full workflow graph on submit, and
the random-prompt predictor logged its in/out — both plaintext, to the app console. Plus ComfyUI's *own* process
logs every submitted graph. Now:

- Both app-side plaintext logs are **gone**, not gated. They were behind a `Logging:LogPrompts` toggle; that key
  was removed outright once the app gained a rolling **file** sink, because a toggle is one setting away from
  putting every prompt on disk permanently. `NoPlaintextLogTests` fails the build if an `ILogger` call so much as
  looks like it emits a prompt-bearing value.
- The prompt actually submitted (and the predictor in/out) is instead written to the per-user **encrypted**
  `UserLog` via `IUserLogService`, gated by **`Logging:AuditUserPrompts`** (default **off**). The interface lives
  in Domain so the Forge worker — which holds the owning `userId` and references Domain only — can resolve it;
  `ComfyClient` (a shared singleton with no `userId`) is *not* where this happens.
- **ComfyUI's own logging is outside the repo and outside the DB** — it's a per-machine OS concern. However ComfyUI
  is started on a given box, its stdout/stderr should go to `NUL`/`/dev/null`: it logs every submitted graph, prompt
  included. That is a per-machine step an operator applies, not something this repo installs — the scripts that once
  registered it (`install-services.ps1`) are gone.

---

### 6.2 Two database engines — the rules that keep them equivalent

`Database:Provider` selects **SqlServer** (default) or **Sqlite**. The claim "runs on either" is worth exactly
as much as the proof, so the entire test suite is engine-agnostic — no test names a provider — and both runs
must pass:

```bash
dotnet test                                     # SQLite, no DB server required
IMAGEGEN_TEST_SQLSERVER=1 dotnet test           # the same tests against SQL Server LocalDB
```

Five things are load-bearing and none of them fail loudly on the *other* engine, which is why they are written
down rather than left to be rediscovered:

- **Encrypting inside a transaction deadlocks SQLite.** `IUserCipher` provisions a user's key on its *own*
  connection, and SQLite permits one writer — so a repository that opens a transaction and then encrypts
  blocks against itself until the busy timeout. Every such site calls `await _cipher.EnsureKeyAsync(userId, ct)`
  **before** `BeginTransactionAsync`; after that first call it is a dictionary hit. **Any new transaction that
  encrypts must do the same.** The symptom is a 30-second hang and `SQLite Error 5: database is locked`, and it
  cannot reproduce under SQL Server.
- **SQLite DDL is not symmetric with DML.** The connection attaches the database file `AS dbo`, which is what
  lets every `dbo.`-qualified statement resolve unchanged on both engines. But in `schema.sqlite.sql`:
  `CREATE TABLE dbo.X` is qualified (fine); `CREATE INDEX dbo.IX_x ON X` carries the schema on the **index
  name** and the indexed table must **not** be qualified; and `REFERENCES X(Id)` can **never** be qualified,
  because SQLite foreign keys cannot cross databases. All three are pinned by `SqliteAttachSpikeTests`.
- **`last_insert_rowid()` alone is a data-integrity bug.** It returns the *previous* insert's id when a guarded
  `INSERT … WHERE NOT EXISTS` matched nothing, so the `changes() = 0` guard in
  `SqliteDialect.InsertedIdentityOrNull` is load-bearing: without it a duplicate registration returns a
  real-looking id and is reported as a new account.
- **`COLLATE NOCASE` on `AppUser.Username` is load-bearing.** SQL Server's default collation is
  case-insensitive and the schema has always relied on it. Remove that one word from the SQLite schema and
  `Bob` and `bob` both register, with login resolving by row order.
- **`IMGDB001`/`IMGDB002` fail the build on purpose.** No provider-typed database reads — use
  `DbValueExtensions`. `IMGDB002` is the one that matters: `ExecuteScalar` returns `object`, SQLite's
  `COUNT(*)` boxes a `long`, and `(int)` on a boxed `long` throws. (`IMGDOC001` is the unrelated rule that
  declaration comments must be `///`.)

---

## 7. Image generation — the job lifecycle

The `ImageGen.Forge` backend turns a generation request into a queued job, renders it on ComfyUI one at
a time, persists the result, and streams progress.

**Who consumes it (and the no-loopback rule).** `ImageGen.Forge` is a set of in-process classes
(`RenderOrchestrator`, the tag model bundle, `IComfyClient`/`ComfyClient`, `WorkflowCatalog`/`WorkflowRegistry`).
`ForgeEndpoints` projects them onto HTTP under `/forge` purely for **out-of-process** callers:

- the **browser UI** (same-origin; `window.GATEWAY = "/forge"`), and
- the **external MCP** (a separate process on 204; `X-Api-Key` → `Forge:ApiKeyUserId`). This is why
  `/forge` is exposed through the public edge at all.

> **Canonical rule:** the app's own server-side code **never calls `/forge` over HTTP** — there is no
> loopback. `/forge` exists only because the browser and the MCP are out-of-process. (Verified: no
> server-side HTTP client targets `/forge`.)

### 7.1 The queue (`JobQueue`)

A **job is a live projection of ComfyUI's state**, not a store of results, and not a thing with a TTL. A
`RenderJob` is the unit a user submits together: a lone `/generate` or `/edit` is a **1-slot** job; a batch is
an **N-slot** job (one slot = one image = one ComfyUI prompt). The queue advances each slot against ComfyUI and
**finalizes** the job only when every slot is terminal, at which point it leaves the active feed.

- A singleton `BackgroundService`. In-memory it holds `_jobs` (active jobs by jobId), `_byOwner` (pending
  **slots** per user), `_lastServed` fairness ticks, `_comfyToSlot` (ComfyUI-prompt-id↔slot), and `_running`
  (the one slot rendering now). **This in-memory state is a write-through cache over the database** (§6,
  `dbo.Job`/`dbo.JobSlot`): every transition is persisted, so a job survives an app restart (rehydrated on
  startup) and a finalized job is recoverable by id after it leaves memory.
- **No TTL, no assume-done.** A finished job is not "retained for 2h then pruned" — it is *finalized and
  removed from the active set the instant its last slot resolves*, and its durable record lives in the DB. A
  slot is never assumed complete: the worker polls `/history/{promptId}` for its result and probes `/queue`
  for **liveness**, so a prompt ComfyUI has LOST (it restarted) is failed after a short confirm-across-polls
  debounce instead of polled forever — which is exactly what used to wedge the whole queue (§10.1).
- **Fair scheduling:** a single worker renders **one slot at a time**, picking by **least-recently-served**
  user (a newcomer with one image beats a user mid-batch; ties break to the oldest queued slot's job).
  Scheduling per-slot means a 10-image job interleaves with other users rather than monopolizing the GPU.
- **"Running" means on the GPU (invariant #13).** A slot becomes `Running` only when ComfyUI's `/queue` reports
  its prompt in `queue_running`, and only for the slot the worker holds — so being picked, having a prompt
  built, or waiting in ComfyUI's *own* queue all read as **queued**, and at most one slot per instance is ever
  running. `RenderPhases` is the single derivation every view uses; the GPU-truth is never persisted (§6) and
  never inferred from a row. The drain probe is the one place that deliberately asks the broader question
  ("anything in flight?"), because a deploy must not stop the app mid-submit either.
- **Ownership / instance:** `EnqueueJobAsync(owner, …)`, `ActiveForOwner(owner)`, and `Cancel` are
  user-scoped; `/interrupt` stops the one image rendering now. Each job carries the **owning `MachineName`**
  (this instance); only the owning instance reconciles, advances, or finalizes it (§8, invariant #4). The
  durable row exists (all-Queued) before any slot becomes schedulable, so transitions persist in order.

### 7.2 The ComfyUI bridge (`ComfyClient`)

- Talks HTTP to ComfyUI (`ComfyUI:BaseUrl`): `POST /prompt`, poll `GET /history/{id}`, fetch `GET /view`,
  `POST /upload/image`, `POST /interrupt`, probe `GET /system_stats` (VRAM) and `GET /object_info/*`
  (loadable files). HTTP calls are **stateless** — any instance can submit/poll.
- Holds **one fixed `client_id` minted per process**. ComfyUI's progress WebSocket routes frames *by
  client_id*. This matters in §8.
- No request timeout: a render takes as long as it takes, bounded only by the job's cancellation.
- **Submitting the graph does not log it.** The full-workflow log (which embeds the user's prompt) was removed
  along with the `Logging:LogPrompts` toggle that gated it; the prompt goes to the encrypted `UserLog` from the
  worker instead (§6.1). `ComfyClient` is a shared singleton with no `userId`, so it never writes the audit log.
- **It builds no graphs itself.** A submit resolves the request's **configuration** (the `model` field is a
  configuration id) to its `IWorkflow` via the `WorkflowRegistry`, merges the configuration's parameter
  settings layer over the workflow's defaults (plus any request `overrides`), resolves the configuration's
  requirement links to filenames, and asks the workflow to build the graph (§7.6). Graph topology lives in
  the per-model workflow classes, never here.

#### 7.2.1 Renderer patches (`ImageGen.Comfy/Patches`, `ImageGen.Web/Comfy`)

Everything this app changes in a ComfyUI **installation** is a `ComfyPatch`: the core quantised-controlnet fix,
the node packs the repo owns, and the fixes it carries for third-party packs. One mechanism, so every change is
listable, removable, and visible — the previous arrangement copied node packs in from a Dockerfile, where a
change was invisible until something built on it broke.

- **Two sources, one type.** `comfy-patches/*.patch` are authored diffs (header, `---`, unified diff);
  `comfy-nodes/<pack>/` + `packs.json` are synthesised into add-everything diffs **in memory**, so the shipped
  patch cannot drift from the tree that is edited and there is no generated artifact to keep in step.
- **State is derived, never stored.** A patch is *applied* exactly when it reverse-applies cleanly; *not
  applied* when it forward-applies; *target missing* when the pack it patches is absent (a `Source:`/`Rev:`
  patch downloads that pinned commit); *conflicted* otherwise, naming the file and hunk. A stored flag can
  disagree with the files, and when it does it is the flag that gets believed.
- **All-or-nothing, no fuzz.** `PatchApplier` resolves the whole patch in memory and writes only once it all
  fits; hunks match context exactly but tolerate line-number drift, since upstream moves code constantly.
  Line endings of the destination are preserved — a Windows checkout is CRLF and a Linux-authored patch is not.
- **The path needs `ComfyUI:Path`.** The app has only ever known the renderer as a URL, and a URL cannot say
  which directory this process may write to. Unset (the renderer is another box) simply means no patches to
  manage, which `/settings/patches` says rather than pretending.
- **One engine, two front-ends.** `tools/ComfyPatch` runs the same `PatchInstaller` during the image build that
  the settings page runs at runtime, so a container cannot end up in a state its own UI misreads. A patch that
  will not apply **fails the build** — the image pins `COMFYUI_REF` and upstream moves.
- **Restart** is `ComfySupervisor`, and only where the deployment supervises ComfyUI: the entrypoint writes
  `comfy.pid`, the app writes a `comfy-restarting` marker and SIGTERMs it, and the entrypoint restarts rather
  than tearing the container down. Without the marker an exit is still a crash and still stops the container.
  Outside the container the page says a restart is needed and leaves it alone — killing a process it did not
  start is how the renderer ends up simply gone.

### 7.3 Endpoints (`ForgeEndpoints`, under `/forge`)

`/generate`, `/edit` (submit a 1-slot job; return its `jobId`), `/enqueue` (submit an **N-slot** job — one
`jobId`, not N; owner-scoped; `model` = a workflow configuration id) · `/result/{id}` (legacy single-image
poll; owner-scoped; memory then DB) · **`/jobs`** (this user's **ACTIVE** jobs only — each with `total`, `progress`, and the
positional `imageIds[]` the client diffs; a finalized job has LEFT this feed) · **`/job/{id}`** (one job by
id, active or finalized — the durable lookup the client makes when a tracked job vanishes from `/jobs`, to
collect its final image array) · `/cancel/{id}`, `/interrupt` · `/upload` · `/image/{id}` (DB-first bytes,
`?w=N` cached thumbnail) · **`/workflows`** (the authoritative list — every configuration this machine can
run, VRAM- + presence-gated, with each configuration's UI-exposed parameters) · `/tags`, `/prompting` ·
**`/ws`** (live progress). (The old `/models` + `/catalog` are gone — see §7.6.)

> **The realtime contract:** `/jobs` and `/job/{id}` carry image **ids, never payloads**. The client DIFFS
> successive reads — a new non-null entry in a job's `imageIds[]` means "a new image exists, fetch
> `/image/{id}`"; a job vanishing from `/jobs` means "finalized, reconcile from history". Completion is a
> doorbell, never the delivery of the image, and never the source of truth (that is `/api/history`).

### 7.4 The `/ws` progress proxy

`ComfyProgressListener` owns the process's single upstream ComfyUI socket (as its `client_id`). It publishes
complete text and binary messages into an in-process bounded fan-out; `/forge/ws` browser sockets subscribe to
that stream and never open competing upstream connections. For each downstream, the fan-out:

- Forwards only frames whose `prompt_id` resolves to **this user's** job (`ResolveProgressRoute`), and **rewrites**
  the ComfyUI `prompt_id` to our `jobId` (the browser never sees ComfyUI ids).
- Associates ComfyUI's legacy binary preview message with the prompt-bearing event immediately before it, then
  gates the preview so a user can't see another user's in-progress thumbnail.
- Drops the oldest messages for a stalled subscriber instead of letting one tab block renderer progress; `/jobs`
  polling remains the authoritative completion path.

The filtering relies on the in-memory `_comfyToSlot` route map — i.e. it only works for jobs *this
instance* submitted (§8).

### 7.5 Server-side history persistence

History survives the originating browser tab closing because the render worker writes it directly when each image is
stored. The durable `JobSlot` already carries the effective prompt, model/config id, aspect, marks, and generated image
id needed for that write; the browser only reads history and treats job completion as a refresh signal. There is no
client-side pending registration, second history writer, or polling reconciler. `HistoryEntry` still deduplicates on
`(UserId, GatewayImageId)` as a final storage invariant.

### 7.6 Workflows, configurations & requirements (the model is workflow-focused)

The system is **workflow-focused**, not model-focused. Three concerns are kept separate (this replaced a
single `models.json` whose entries fused all of them and a `ComfyClient` that dispatched on big if/else
chains):

- **Workflow** — a C# class (`IWorkflow`, under `Forge/Workflows/`) that builds one ComfyUI graph topology.
  It owns its graph explicitly, declares the full set of parameters it understands (`Schema`), a `Kind`
  (Generate/Edit), and its media/capability contract. **One workflow per model — workflows are
  never shared between models.** There are ~26: one txt2img class per generation model (over a shared
  `Txt2ImgWorkflowBase` topology) and one self-contained class per edit model (Flux Kontext, Flux.2, Qwen,
  Wan i2v, AnimateDiff sd15/sdxl, LTX-V). They share only low-level emit primitives (`ComfyGraph`:
  `Node`/`Ref`/sampler maps), never graph logic. Registered explicitly in `AddWorkflows()` → `WorkflowRegistry`.
- **WorkflowConfiguration** — a row of **`workflows.json`** and the unit the API exposes. It binds one
  workflow by name, supplies its **settings layer** (`params`: a value per key + an `exposed` flag deciding
  whether the UI gets a control or it's a retained hidden default), soft-links its **requirements** by id,
  and carries the decision-card/prompting metadata (`card`). `id` is the unique
  key the client submits as `model`; `friendly_name` MAY be shared across configurations.
- **Requirement** — a row of **`requirements.json`**: a model file (`name`) with a `kind`, `target_folder`,
  download `urls` (carried for a future fetcher — not downloaded yet), and optional size. Configurations
  soft-link them by id; deduped by filename, so a shared VAE/encoder is one requirement many configs link.

`WorkflowCatalog` loads both files (hot-reloaded on change), resolves a configuration's requirement links to
filenames, and serves the cards. Both files default under the repo root via `Catalog:WorkflowsPath` /
`Catalog:RequirementsPath`.

> **Adding or upgrading a model — every model file MUST be registered in `requirements.json`.** A
> configuration only references files *by requirement id*; the actual on-disk filename, kind, target folder and
> download URL live in `requirements.json`. So the full recipe is: **(1)** add a row to `requirements.json`
> for each new file the model needs (the diffusion model **and** any new VAE / text-encoder / LoRA — shared
> files are reused by id, not duplicated); **(2)** add the configuration to `workflows.json`, soft-linking
> those requirement ids; **(3)** only if its ComfyUI graph topology is genuinely new, write a workflow class
> and register it in `WorkflowRegistration.AddWorkflows()` — most models reuse an existing class (the generic
> `Txt2ImgWorkflowBase`/`EditWorkflowBase` already cover checkpoint/unet/unet_gguf loaders + single/dual CLIP).
> A configuration whose linked files aren't in `requirements.json` resolves to empty filenames and is hidden
> by presence-gating — i.e. it silently never appears. This is the step that's easy to forget.
>
> **Precision / VRAM tiers are an operator choice.** The catalogue has no VRAM metadata and
> `min_vram_mb` / `max_vram_mb` are not valid configuration keys. Bind and expose only configurations suitable
> for the renderer. Configurations with the same `friendly_name` are de-duplicated in catalogue order after
> requirement-presence filtering; the server does not choose between them from GPU capacity.
>
> **OOM is not a failure mode here — it's an operating assumption.** Every machine this runs on is provisioned
> so its offered configuration fits, and the hosts carry enough system RAM that ComfyUI's weight-offload absorbs
> anything tight. So a model may *spill*
> (offload to host RAM and run slower) but will **not hard-OOM/crash** on its intended machine. Concretely, on
> the 24 GB box the high tiers run at Q8/bf16 (e.g. Qwen-Image Q8 ~20 GB + its encoder) — sized to spill at
> worst, never to OOM. Don't add code that treats OOM as a recoverable runtime case; if a new tier could OOM a
> target machine, do not bind/expose that configuration on the machine.

**The list the API serves (`GET /forge/workflows`) is presence-gated.** A configuration is offered only when its
workflow class is registered and every requested requirement is usable: custom-node requirements must be reported
by ComfyUI, while model slots must be bound to files ComfyUI reports present. Configurations that share a
`friendly_name` (within a kind/effect/edit section) are de-duplicated by keeping the first eligible catalogue entry;
there is no GPU-capacity selection. Each row carries the configuration's UI-exposed
parameters (joined to the workflow schema for type/range/label); the SPA renders them as controls and sends
their values back as `overrides` on generate/edit.

---

## 8. Cross-device & multi-instance: durable vs process-local

Two different axes, deliberately handled differently:

- **Cross-device (same instance):** two browsers, one user, both hitting 204. **Works**, because the
  shared truth is the DB *and* the one in-memory `JobQueue` they both reach. This is what the recent
  `liveSync` work fixed: the client reconstructs live state from the server (`/jobs` + `/ws`), not from
  device storage. See §9.
- **Multi-instance (204 + 206):** two app *processes*, **each with its own GPU, its own ComfyUI, and its
  own `JobQueue`**, sharing **only the database**. This is intentional: 204 and 206 are independent
  generators that pool their *data and history* (the DB), not their *compute*. Everything in §6 is
  visible to both; everything in §7.1's in-memory state is private to each — **by design**. A user's
  generation session is instance-local: the instance you submit to is the one that renders, streams
  progress, and answers `/jobs` for that job. Cross-instance live job visibility is **not a goal**, and
  there is no load balancer splitting a session across instances (the public edge routes only to 204;
  206 is reached directly).

### 8.1 What is shared vs not

| State | Where | Shared across instances? |
|---|---|---|
| Users, history, bookmarks, bans, artist displays, settings, **image bytes**, pending rows, **job + slot records** | SQL DB (204) | ✅ yes (durable; rows tagged with owning `MachineName`) |
| Live job *working set*: per-user slot queues, fairness, `_comfyToSlot`, running slot | `JobQueue` memory (cache over `dbo.Job`) | ❌ **per-process** — only the owning instance advances/finalizes/reports a live job |
| ComfyUI `client_id`, the upstream `/ws` connection | `ComfyClient` memory | ❌ **per-process** |
| Tag model + vocabulary, thumbnail cache | `TagModelBundle` / `IMemoryCache` | ❌ per-process (fine — derived/cacheable) |

`dbo.Job` rows are now in the shared DB, but a job is still **rendered, advanced, and reported only by its
owning instance** (the `MachineName` it carries). The active `/jobs` feed reads that instance's in-memory
working set; `/job/{id}` may read a finalized row from any instance (durable). No instance reconciles another
instance's live job against *its* ComfyUI — that would be meaningless (different GPU/queue). This is invariant
#4 intact: instances share *data* (now including job rows), never *live job control*.

### 8.2 What this means (correct, intended behavior)

- A user's jobs, progress (`/ws`), and `/jobs`/`/result`/`/cancel` answers come from **the instance they
  submitted to**. Each instance has its own ComfyUI `client_id` and `_comfyToJob` map, so its `/ws` only
  carries its own jobs. Fairness (least-recently-served) is per-instance — correct, because each instance
  drives its own GPU. `liveSync` reconstructs live state from `/jobs`+`/ws` and is correct **within the
  serving instance**, which is the supported model.
- The **only** thing that must cross instances is the durable per-user data in §6, and it does, through
  the shared DB.

### 8.3 The cross-instance ownership rule

Each instance may advance only durable jobs whose `MachineName` names that instance. Rehydration filters on that key,
and the worker writes image history while processing its own slots; there is no separate shared pending-work list for
another instance to reap or interpret.

> **Canonical rule:** instances share *data* (the DB), never *live job state*. No instance may delete,
> persist, or report a job it does not own. Cross-instance coordination happens only through durable,
> owner-scoped DB rows — never by one instance reasoning about another's in-memory queue.

---

## 9. The composer & realtime UI

The composer is a reusable Razor partial (`_Composer.cshtml`) + `compose.js`, embedded on the main
page and the artist page (artist-locked mode). Other pages own their own grids and just listen.

**Submission:** `generate()`/`runGeneration()` → `POST /forge/generate`; batches →
`POST /forge/enqueue`. Requests carry the model, prompt, aspect, the random-artist/random-prompt toggles,
and the temperature. They do **not** carry the user's bans: a ban is a server-side fact, so the render worker
reads the banned tags/artists for (user, workflow) from the store at render time
(`RenderOrchestrator.BannedKeysAsync`). A caller therefore cannot generate around a ban by omitting it — not an
API-key client, not a browser holding a stale cache, not a job resumed from before the ban was saved. Bans
suppress tokens in **auto-gen only** — a typed token is never stripped.

**The realtime contract (one event, many listeners — a trigger, not a payload):** when an image is known to
exist, code dispatches a `imagegen:generated` CustomEvent `{ id, prompt, marks, model, modelId, aspect, ts }`,
and the cross-device tracker fires `imagegen:refresh` when a job finalizes. These are **triggers to reconcile
from history**, not the data to render. `recents.js` re-pulls `/api/history` (page 1) on any trigger and
renders **strictly from that** — so a deleted image stays gone and a stale/lingering job can never resurrect
one (the event carries no id the strip renders from; it is purely a cue to re-pull). `artist.js` still
optimistically adds a genuinely-new image that carries this artist's mark **and no other artist's**, and
reconciles from history on reload.

**The card outline means UNVIEWED, not "just generated":** `dbo.ImageView` is a row per (user, image) written
when the user opens an image — the `/image/{id}` page or the lightbox's card fetch, both of which come through
`ImageController.BuildAsync`. Absence is the unviewed state, so a newly generated image is outlined without
anything writing a row for it, and the outline then survives a reload and is the same on every device. This
replaced an in-memory `fresh` set that meant "generated while this tab was open": never cleared by looking at
anything, empty again after a reload, and present on only one of the two grids.
Persistence (`postHistory`) happens **before** the signal, so the re-pull sees the new image.

**`liveSync` — the cross-device tracker (always on):** polls `/forge/jobs` (active jobs only) + `/forge/ws`
for progress, and **diffs** successive reads: a new id in a job's `imageIds[]` → announce it (highlight +
history reconcile, and reflect it in the top preview); a tracked job that has **vanished** from the feed →
finalized → fetch `/forge/job/{id}` for any straggler, then signal a reconcile. It also reflects the active
gen (busy/progress) and defers to the local Generate flow while *this* tab is generating. Because the feed
returns only active jobs, a finished job is never re-announced — the old "lingering done job re-appends to
Recents" bug (§10.1) is structurally impossible now.

A **job is the batch** now (one `jobId`, N slots), so its own `total`/`progress` drive the same "Creating X of
N" (1-indexed — the one being made now) and the same progress bar on every device, with no originator
privilege and no separate `batchId` to group (invariant #10).

> **Canonical rule (the one that was being violated):** **the browser holds no authoritative user
> state.** All user-meaningful state is per-user in the DB; the server (`/jobs`, `/ws`, `/api/*`) is the
> source of truth; the UI reconstructs from it on load/focus on *every* device. `localStorage`/
> `sessionStorage` may hold only ephemeral, non-authoritative, single-tab scratch state. If losing it on
> another device would surprise the user, it does not belong in device storage — it belongs in the DB
> keyed by user id.

State that correctly moved server-side: composer draft (prompt/model/aspect/random-artist toggle/random-prompt
temperature) → `AppUser.ComposerPrefs` via `/api/settings/composer`; history/bookmarks/bans/artist-displays → their
tables.

`GET /api/settings` is read-only — there is no `PUT /api/settings`. Every writable preference owns its own route and
its own column, so one autosave can never clobber another's.

---

## 10. Known inconsistencies & design-pattern violations

These are where the code contradicts the canon above. Fixed items are kept (struck through) as a record.

### 10.1 Fixed

0. ~~**Jobs were in-memory-only with a broken TTL; finished jobs lingered and re-fed "Recents"; a ComfyUI
   restart wedged the queue (correctness, was the worst).**~~ **FIXED — the job-lifecycle rework (§7.1).**
   `JobQueue` pruning ran only on enqueue (never on a timer), and never expired non-terminal jobs, so finished
   jobs from hours/days earlier sat in memory; `/jobs` returned them as `done`; the front end re-appended them
   to the Recents strip on every poll — and because the strip rendered the job payload rather than history, a
   *deleted* image resurrected on refresh. Separately, the worker polled `/history/{id}` with no liveness check,
   so a prompt ComfyUI lost on restart was polled forever, head-of-lining the whole serial queue. Now: jobs are
   **DB-backed, slot-based, reconciled against ComfyUI** (`/queue` liveness, no TTL), finalize-and-leave-the-feed
   on completion, and the front end treats completion as a doorbell — Recents renders strictly from
   `/api/history`, so deletes stick and nothing resurrects.

1. ~~**`PendingJobReconciler` cross-instance deletion (correctness, was the worst).**~~ **RETIRED.** The
   render worker now writes history itself from the authoritative typed job/slot rows. The client pending POST,
   reconciler, service/repository stack, and configuration toggle were removed rather than preserving a second
   lifecycle beside `Job`. The old table's released CREATE blocks remain untouched in the append-only schema history;
   no runtime code reads or writes that inert legacy artifact.
2. ~~**`edit.js` used `localStorage` for the edit workflow (violated §9).**~~ **FIXED.** Moved to a per-user
   `AppUser.EditWorkflowId` column via `GET /api/settings` + `PUT /api/settings/edit-workflow`; restored on
   boot, saved on change. No composer/edit state remains in device storage.
3. ~~**Stale `README.md` (ForgeGateway `:5079`, `Gateway:BaseUrl`).**~~ **FIXED.** README now describes the
   embedded `/forge` backend and the real config keys, and points here.
4. ~~**The second machine's example config drove the first's ComfyUI/tag model.**~~ **FIXED, then moot.** A second
   instance renders on its own GPU, so it is an independent generator depending on the first only for the database.
   The per-machine example configs have since moved out of the repo entirely (they described specific boxes), and
   `Tags:ModelUrl` no longer exists — the tag model is in-process.

### 10.2 Open

5. ~~**`Forge:ApiKeyUserId` unset in both example configs though required when `Forge:ApiKey` is set.**~~
   **Moot.** Both keys are dead — auth moved to per-user `AppUser.ApiKey` (see §5). The stale references
   have been removed from the docs and example configs.

6. ~~**`uninstall-services.ps1` doesn't remove the `ImageGen-TagModel` task.**~~ **Superseded.** The tag
   model is moving in-process (ONNX Runtime in the app), so the task, the venv and port 8000 all go away
   rather than needing to be uninstalled. The service scripts themselves are becoming operator-local
   tooling and leave the repo.

### 10.3 Documented, not a bug

7. **`compose.js` `makeapicture_batch` is device-local (by design).** Tracks the in-flight batch's single
   `jobId` + which slot ids it has recorded, for *this* tab. Not authoritative (results persist server-side;
   `liveSync` gives cross-device visibility), so it's the one sanctioned piece of ephemeral single-tab
   scratch — never relied on across devices.

8. **`ImageBlobRepository`/`JobRepository` are Singletons while every other repository is Scoped.**
   Deliberate, because the singleton `JobQueue` persists images and writes jobs through. Documented in §4 so
   nobody "fixes" them into Scoped services (which would make a singleton depend on a scoped service) — and so
   any new `JobQueue` persistence follows the same singleton-safe pattern.

9. **Deterministic token columns leak equality (by design, §6.1).** Encrypting `HistoryMark.Token` etc.
    deterministically is what keeps tag/artist filtering and UNIQUE constraints working without server-side
    decryption — but it means someone with DB read access can see which rows share a (still-secret) token and
    frequency-analyze. This is an accepted trade-off given the "accidental viewing" threat model, not a defect;
    prompts (randomized) don't leak it. Strengthening it would mean giving up searchable tags.

11. **Ideogram 4 carries a first-step conditional-model correction.** The `ideogram4` generation graph and
    `ideogram4-refine` whole-image img2img graph load separate conditional and unconditional fp8 UNets. The refine
    graph VAE-encodes its source and selects the low-sigma tail with `SplitSigmasDenoise`; both graphs pass only the
    conditional model through the first-party
    `Ideogram4CorrectionPatch` node, then through `CFGOverride`, before `DualModelGuider` combines it with the
    untouched unconditional model. The node rotates image-token residuals at blocks 25–28 during step 0/pass 0
    using one frozen direction at strength 0.6 and restores each token's norm. It clones the in-memory model
    patcher and never writes checkpoint weights. The node pack and its 2.36 MB tensor bundle live under
    `comfy-nodes/ComfyUI-Ideogram4Debanner`; `comfyui-ideogram4-debanner` presence-gates both workflows so a fresh
    renderer cannot advertise either graph when its custom node is absent. The paired reversible
    `core-ideogram4-block-patch` adds the otherwise-missing residual hook; the node pack registers nothing when
    that capability marker is absent, so missing the core patch also keeps the workflow unavailable.

---

## 11. Invariants — the short list

1. All user-meaningful state lives in the shared SQL DB on 204, keyed by user id. The browser holds no
   authoritative state.
2. The server is the source of truth for live state (`/jobs`, `/ws`); every device reconstructs from it. A
   **job is a live projection of ComfyUI's state**, reconciled against ComfyUI (never assumed done, no TTL),
   write-through to the DB; **completion is a doorbell, not a payload** — the client diffs the job's
   `imageIds[]` and a vanish from the feed, and the source of truth for *completed* images is `/api/history`,
   never a job. No store of finished results, no resurrecting a deleted image.
3. Ownership is resolved server-side from the authenticated principal; clients never assert it.
4. Instances share *data* (the DB), never *live job state*. Each instance generates independently on its
   own GPU/ComfyUI/queue; a generation session is instance-local. No instance may delete, persist, or
   report a job it does not own — cross-instance coordination is only through owner-scoped DB rows.
5. Bans/auto-gen suppression affect random generation only — never a token the user typed.
6. History/bookmark writes are idempotent (dedupe on `(UserId, GatewayImageId)` / unique keys); the
   direct write and the reconciler write must remain safe to both fire.
7. Dependencies point inward to Domain; a Singleton never depends on a Scoped service.
8. `schema.sql` is idempotent and additive; the app login has no DDL — schema changes are applied
   out-of-band.
9. In-process code uses the render/Comfy classes directly — it never calls `/forge` over HTTP. The
   `/forge` HTTP surface exists only for out-of-process callers (the browser same-origin, and the MCP
   publicly via `X-Api-Key`). No loopback.
10. **Same experience on every device, regardless of which one started the job.** Status text, progress,
    and appended results must be identical on the originating tab and any observing tab — both reconstruct
    from server state (`/jobs` grouped by `batchId`, `/ws`), never from the originator's local state. A
    batch shares one server-assigned `batchId`, so any device groups it and shows the same "Creating X of
    N"; no device may render a generation differently because it happens to hold the originating
    `localStorage`. (Corollary of invariants 1–2.)
11. **One workflow class per model; the API lists configurations, not models.** A workflow owns its graph
    and is never shared between models. A configuration (`workflows.json`) binds one workflow, supplies its
    parameter settings layer + UI-exposed flags, and soft-links its requirements (`requirements.json`). A
    configuration is offered by `GET /forge/workflows` only when its workflow class exists and every requested
    node/model requirement is present and bound as appropriate. Configurations sharing a `friendly_name` are
    de-duplicated by catalogue order after that presence check; GPU capacity is not part of eligibility. Graph
    construction never lives in `ComfyClient` — it dispatches to the workflow.
12. **Prompt/tag data is encrypted at rest, per-user, with the repositories as the only crypto boundary.**
    Keys live in their own `dbo.UserEncryptionKey` table (never on a queried entity); free text is randomized,
    searchable tokens are deterministic, decrypt tolerates legacy plaintext (§6.1). Code outside the repos sees
    only plaintext and must never persist a sensitive value by a path that bypasses the cipher. Prompts must not
    reach plaintext logs: the app-side prompt sinks were removed outright (there is no toggle to re-enable them,
    and `NoPlaintextLogTests` fails the build on a new one), and ComfyUI's own process logging is silenced
    per-machine by the operator. Backfilling existing rows is a standalone throwaway EXE, never code that ships
    in the app.
13. **"Running" means the GPU is generating that image right now — nothing weaker, and never more than one.**
    The box renders one slot at a time, so at most one job per instance may report it. It is entered only on
    ComfyUI's own report that the prompt is in `queue_running`, only for the slot the worker holds, and it is
    **never persisted** — a durable row cannot keep "on a GPU" true past the writing process's life, which is
    how crashed and orphaned jobs came to claim they were rendering forever. Everything else that is merely
    unfinished — picked but not submitted, sitting in ComfyUI's own queue, a batch with some slots done, an
    `Active` row no live worker owns — is **queued**. One derivation (`RenderPhases`) serves every view; a
    status computed anywhere else is the bug this invariant exists to prevent. A separate, deliberately
    broader "in flight" count exists for the deploy drain only (§7.1).
