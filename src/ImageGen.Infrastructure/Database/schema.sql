-- ImageGen database schema. Idempotent: safe to run repeatedly.
-- Applied by setup-database.ps1 (via sqlcmd) and by DatabaseInitializer (for tests/dev).

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AppUser' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.AppUser
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AppUser PRIMARY KEY,
    Username     NVARCHAR(256)  NOT NULL,
    PasswordHash NVARCHAR(512)  NOT NULL,
    DisplayName  NVARCHAR(256)  NOT NULL,
    CreatedAtUtc DATETIME2(3)   NOT NULL,
    CONSTRAINT UQ_AppUser_Username UNIQUE (Username)   -- default CI collation = case-insensitive
);
GO

-- (RandomPromptTemp used to be added here. The random-prompt temperature is now the composer's own slider, carried
-- per generation inside ComposerPrefs below. Existing databases drop the column via
-- scripts\drop-random-prompt-temp.sql -- this file stays additive and never drops data.)

-- Per-user composer state (draft prompt, model, aspect, random-artist toggle, random-prompt temperature) as an opaque
-- JSON blob written by the composer, so the composer follows the user across devices. NULL = unset (defaults).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'ComposerPrefs' AND Object_ID = Object_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD ComposerPrefs NVARCHAR(MAX) NULL;
GO

-- Per-user editor state (active mode/tab, selected edit workflow(s), inpaint workflow, a flat by-name param-override
-- map shared across workflows, brush size) as an opaque JSON blob written by the editor, so the whole edit page
-- follows the user across devices -- the edit-page analogue of ComposerPrefs. ENCRYPTED at rest (like ComposerPrefs).
-- NULL = unset (the editor starts from defaults). Supersedes the old EditWorkflowId column (now unused; left in place
-- on existing databases since this schema is additive -- drop it out-of-band if desired).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'EditPrefs' AND Object_ID = Object_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD EditPrefs NVARCHAR(MAX) NULL;
GO

-- Per-user favorited workflow (configuration) ids as an opaque JSON array — favorites sort to the top of the workflow
-- pickers with a star. NULL = none. Not sensitive, stored plain.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'FavoriteWorkflowIds' AND Object_ID = Object_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD FavoriteWorkflowIds NVARCHAR(MAX) NULL;
GO

-- Per-user custom per-workflow tags as an opaque JSON map ({ "workflowId": ["tag", ...] }), shown under each workflow in
-- the pickers / on its workflow page. Personal labels -> ENCRYPTED at rest with the user cipher (like ComposerPrefs).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'CustomWorkflowTags' AND Object_ID = Object_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD CustomWorkflowTags NVARCHAR(MAX) NULL;
GO

-- Per-user hidden workflow (configuration) ids as an opaque JSON array — hidden workflows are dropped from the
-- compose/edit pickers (still listed + toggleable on the Workflows page). NULL = none. Not sensitive, stored plain.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'HiddenWorkflowIds' AND Object_ID = Object_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD HiddenWorkflowIds NVARCHAR(MAX) NULL;
GO

-- Per-user GENERATION MASK: which tag types the model may emit when it generates a random prompt, as a JSON array of
-- type names (["character","copyright","meta"]). Unlike the blobs above the server PARSES this one (the render worker
-- sends it to the tag model), so it is validated on write. Not sensitive, stored plain. NULL = unset, which resolves to
-- the default (artists off). An empty selection is stored as [] -- a real choice, distinct from NULL.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'GenerationTagTypes' AND Object_ID = Object_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD GenerationTagTypes NVARCHAR(256) NULL;
GO

-- The bookmarks page's folded sections, per user so they follow across devices (this state used to live in the
-- browser's localStorage, which is not where any client state belongs here). Opaque JSON the server stores verbatim;
-- the keys name a category and, for a sub-section, the kind within it -- and a category key IS its title, so renaming
-- a category drops its saved fold state. Encrypted at rest: the keys contain the user's own category names.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'BookmarkPrefs' AND Object_ID = Object_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD BookmarkPrefs NVARCHAR(MAX) NULL;
GO

-- Per-user API key (a bare GUID) for non-browser callers: presenting it as the X-Api-Key (or Authorization: Bearer)
-- header authenticates the request AS THIS USER, with the same access as a logged-in browser. It's a bearer secret,
-- so it's stored as-is (lookup is by equality — it can't be a one-way hash) and is NEVER selected into any user-facing
-- response. NULL = no key (the default; the user can only authenticate by cookie until one is provisioned).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'ApiKey' AND Object_ID = Object_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD ApiKey NVARCHAR(64) NULL;
GO

-- Enforce key uniqueness (filtered so the many NULL-key users don't collide) — the key is a login credential.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_AppUser_ApiKey')
    CREATE UNIQUE INDEX UQ_AppUser_ApiKey ON dbo.AppUser (ApiKey) WHERE ApiKey IS NOT NULL;
GO

-- Per-user toggle: pin the user's matching bookmarked tags/artists to the top of the '#'/'@' autocomplete. A plain
-- boolean, not sensitive, stored as a BIT with a default -- 0 = off, so autocomplete is unchanged until the user turns
-- it on. NOT NULL with a constant default so existing rows adopt "off" without a backfill.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'PinBookmarkSuggestions' AND Object_ID = Object_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD PinBookmarkSuggestions BIT NOT NULL CONSTRAINT DF_AppUser_PinBookmarkSuggestions DEFAULT 0;
GO

-- Per-user parameter-visibility overrides (issue #191): which workflow params this user has revealed or hidden on the
-- generation page, as an opaque JSON blob (config id -> param key -> bool) the server stores verbatim. The keys are
-- catalog identifiers, not user content -- stored plain, like GenerationTagTypes. NULL = no overrides (shipped
-- visibility applies).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'ParamVisibilityPrefs' AND Object_ID = Object_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD ParamVisibilityPrefs NVARCHAR(MAX) NULL;
GO

-- Per-user data-encryption key, kept in its OWN deliberately-obvious table (not on AppUser) so routine queries over
-- user/history/bookmark data never pull key material into a result set, and the table name flags it as "don't SELECT".
-- KeyMaterial is a random 32 bytes; the app derives subkeys (HKDF) for randomized + deterministic column encryption.
-- One row per user (PK = UserId). Provisioned lazily on first use and at registration. No master key by design.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UserEncryptionKey' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.UserEncryptionKey
(
    UserId       BIGINT         NOT NULL CONSTRAINT PK_UserEncryptionKey PRIMARY KEY,
    KeyMaterial  VARBINARY(64)  NOT NULL,
    CreatedAtUtc DATETIME2(3)   NOT NULL,
    CONSTRAINT FK_UserEncryptionKey_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'HistoryEntry' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.HistoryEntry
(
    Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_HistoryEntry PRIMARY KEY,
    UserId         BIGINT         NOT NULL,
    GatewayImageId NVARCHAR(256)  NOT NULL,
    Prompt            NVARCHAR(MAX) NOT NULL,   -- the FINALIZED prompt the model rendered (encrypted at rest)
    RawPrompt         NVARCHAR(MAX) NULL,       -- the prompt VERBATIM as submitted, in marker form (encrypted at rest)
    RawNegativePrompt NVARCHAR(MAX) NULL,       -- the negative VERBATIM as submitted, in marker form (encrypted at rest)
    OriginalPrompt    NVARCHAR(MAX) NULL,       -- the prompt as the user TYPED it, pre-expansion (encrypted at rest)
    ModelFriendly  NVARCHAR(256)  NOT NULL,
    ModelId        NVARCHAR(128)  NOT NULL,
    Aspect         NVARCHAR(16)   NOT NULL,
    CreatedAtUtc   DATETIME2(3)   NOT NULL,
    CONSTRAINT FK_HistoryEntry_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_HistoryEntry_User_Image UNIQUE (UserId, GatewayImageId)
);
GO

-- RawPrompt is what the user actually submitted ('#tag, @artist', underscores intact), kept so the copy/reload/edit
-- surfaces can hand the prompt back VERBATIM instead of reconstructing it from the finalized text (a lossy inverse of
-- a lossy transform). Nullable because rows written before it existed had none: those were backfilled once, with the
-- best reconstruction available at the time. Every row the worker writes now carries the real thing.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'RawPrompt' AND Object_ID = Object_ID('dbo.HistoryEntry'))
    ALTER TABLE dbo.HistoryEntry ADD RawPrompt NVARCHAR(MAX) NULL;
GO

-- The negative gets the same treatment as the positive: it is typed in the same marker dialect (the negative box has
-- the same '#'/'@' autocomplete) and finalized the same way, so it must be STORED the same way, or Reload and the edit
-- boxes cannot hand it back. Nothing reconstructs it. NULL = no negative was submitted (which is not the same as "").
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'RawNegativePrompt' AND Object_ID = Object_ID('dbo.HistoryEntry'))
    ALTER TABLE dbo.HistoryEntry ADD RawNegativePrompt NVARCHAR(MAX) NULL;
GO

-- The prompt as the user TYPED it, which despite its name RawPrompt is NOT: two layers of resolution happen first.
-- The composer collapses [a|b] to the option it rolled, fans {a|b} into separate submitted variants, and appends an
-- artist page's locked artist -- all in the browser, before the request is sent -- and the worker then appends its
-- sampled tags/artist in the same marker dialect. Every one of those is one-directional, so the intent is not
-- recoverable from the result. Encrypted at rest like the other prompt columns.
--
-- NULL on every row written before this column, and unlike RawPrompt it CANNOT be backfilled: the pre-expansion text
-- was discarded in the browser and never transmitted. Readers must report "not recorded", never substitute.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'OriginalPrompt' AND Object_ID = Object_ID('dbo.HistoryEntry'))
    ALTER TABLE dbo.HistoryEntry ADD OriginalPrompt NVARCHAR(MAX) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HistoryEntry_User_Created')
CREATE INDEX IX_HistoryEntry_User_Created ON dbo.HistoryEntry (UserId, CreatedAtUtc DESC, Id DESC);
GO

-- Which images a user has actually OPENED. The grids outline an image until it has been looked at, and that has to
-- survive a reload and follow the user to their other devices, so it is a row per (user, image) rather than client
-- state. Deliberately NOT the per-user settings blob: this set grows with the library, and those blobs are for a
-- handful of preferences. Absence means unviewed, so the table only ever holds what has been seen.
--
-- Marking is idempotent (the PK is the identity) and the row carries when it happened, which is the only thing an
-- "unviewed since" question would ever need.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ImageView' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.ImageView
(
    UserId         BIGINT        NOT NULL,
    GatewayImageId NVARCHAR(256) NOT NULL,
    ViewedAtUtc    DATETIME2(3)  NOT NULL,
    CONSTRAINT PK_ImageView PRIMARY KEY (UserId, GatewayImageId),
    CONSTRAINT FK_ImageView_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'HistoryMark' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.HistoryMark
(
    Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_HistoryMark PRIMARY KEY,
    HistoryEntryId BIGINT        NOT NULL,
    Token          NVARCHAR(512) NOT NULL,   -- holds either a plaintext token or its (longer) deterministic ciphertext
    Kind           TINYINT       NOT NULL,
    CONSTRAINT FK_HistoryMark_Entry FOREIGN KEY (HistoryEntryId) REFERENCES dbo.HistoryEntry(Id) ON DELETE CASCADE
);
GO

-- Widen Token to fit deterministic ciphertext on databases created before encryption (idempotent: only when still 256).
IF EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Token' AND Object_ID = Object_ID('dbo.HistoryMark') AND max_length = 512)
    ALTER TABLE dbo.HistoryMark ALTER COLUMN Token NVARCHAR(512) NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HistoryMark_Entry')
CREATE INDEX IX_HistoryMark_Entry ON dbo.HistoryMark (HistoryEntryId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HistoryMark_Token')
CREATE INDEX IX_HistoryMark_Token ON dbo.HistoryMark (Token, Kind);
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'HistoryLora' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.HistoryLora
(
    Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_HistoryLora PRIMARY KEY,
    HistoryEntryId BIGINT        NOT NULL,
    Name           NVARCHAR(512) NOT NULL,   -- the subfolder-qualified lora_name's deterministic ciphertext
    Weight         FLOAT         NOT NULL,
    CONSTRAINT FK_HistoryLora_Entry FOREIGN KEY (HistoryEntryId) REFERENCES dbo.HistoryEntry(Id) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HistoryLora_Entry')
CREATE INDEX IX_HistoryLora_Entry ON dbo.HistoryLora (HistoryEntryId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HistoryLora_Name')
CREATE INDEX IX_HistoryLora_Name ON dbo.HistoryLora (Name);
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TokenBookmark' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.TokenBookmark
(
    Id         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TokenBookmark PRIMARY KEY,
    UserId     BIGINT        NOT NULL,
    Name       NVARCHAR(512) NOT NULL,   -- plaintext name or its (longer) deterministic ciphertext
    Kind       TINYINT       NOT NULL,
    SavedAtUtc DATETIME2(3)  NOT NULL,
    CONSTRAINT FK_TokenBookmark_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_TokenBookmark_User_Name_Kind UNIQUE (UserId, Name, Kind)
);
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Name' AND Object_ID = Object_ID('dbo.TokenBookmark') AND max_length = 512)
    ALTER TABLE dbo.TokenBookmark ALTER COLUMN Name NVARCHAR(512) NOT NULL;
GO

-- Pin a starred artist/tag to the top of the bookmarks page. NULL = not pinned; the timestamp both flags and orders.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'PinnedAtUtc' AND Object_ID = Object_ID('dbo.TokenBookmark'))
    ALTER TABLE dbo.TokenBookmark ADD PinnedAtUtc DATETIME2(3) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ImageBookmark' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.ImageBookmark
(
    Id                   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImageBookmark PRIMARY KEY,
    UserId               BIGINT         NOT NULL,
    GatewayImageId       NVARCHAR(256)  NOT NULL,
    Prompt               NVARCHAR(MAX)  NOT NULL,
    ModelFriendly        NVARCHAR(256)  NOT NULL,
    ModelId              NVARCHAR(128)  NOT NULL,
    Aspect               NVARCHAR(16)   NOT NULL,
    OriginalCreatedAtUtc DATETIME2(3)   NOT NULL,
    SavedAtUtc           DATETIME2(3)   NOT NULL,
    CONSTRAINT FK_ImageBookmark_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_ImageBookmark_User_Image UNIQUE (UserId, GatewayImageId)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ImageBookmark_User_Saved')
CREATE INDEX IX_ImageBookmark_User_Saved ON dbo.ImageBookmark (UserId, SavedAtUtc DESC, Id DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ImageBookmarkMark' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.ImageBookmarkMark
(
    Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImageBookmarkMark PRIMARY KEY,
    ImageBookmarkId BIGINT        NOT NULL,
    Token           NVARCHAR(512) NOT NULL,   -- plaintext token or its (longer) deterministic ciphertext
    Kind            TINYINT       NOT NULL,
    CONSTRAINT FK_ImageBookmarkMark_Bookmark FOREIGN KEY (ImageBookmarkId) REFERENCES dbo.ImageBookmark(Id) ON DELETE CASCADE
);
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Token' AND Object_ID = Object_ID('dbo.ImageBookmarkMark') AND max_length = 512)
    ALTER TABLE dbo.ImageBookmarkMark ALTER COLUMN Token NVARCHAR(512) NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ImageBookmarkMark_Bookmark')
CREATE INDEX IX_ImageBookmarkMark_Bookmark ON dbo.ImageBookmarkMark (ImageBookmarkId);
GO

-- Bookmark categories: any bookmark (starred artist/tag or saved image) can be filed under any number of named
-- categories, shared across both kinds. A category is just a name that exists because a bookmark references it -- there
-- is no separate category table and no empty categories. The name is deterministically encrypted like the parent's
-- Name/Token (the owning user is threaded in from the parent row), so the distinct-category list works on ciphertext.
-- A bookmark with no rows here lives in the "Global" (uncategorized) bucket. Cascades from its one parent only.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TokenBookmarkCategory' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.TokenBookmarkCategory
(
    Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TokenBookmarkCategory PRIMARY KEY,
    TokenBookmarkId BIGINT        NOT NULL,
    Category        NVARCHAR(512) NOT NULL,   -- plaintext category name or its (longer) deterministic ciphertext
    CONSTRAINT FK_TokenBookmarkCategory_Bookmark FOREIGN KEY (TokenBookmarkId) REFERENCES dbo.TokenBookmark(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_TokenBookmarkCategory UNIQUE (TokenBookmarkId, Category)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TokenBookmarkCategory_Bookmark')
CREATE INDEX IX_TokenBookmarkCategory_Bookmark ON dbo.TokenBookmarkCategory (TokenBookmarkId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ImageBookmarkCategory' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.ImageBookmarkCategory
(
    Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImageBookmarkCategory PRIMARY KEY,
    ImageBookmarkId BIGINT        NOT NULL,
    Category        NVARCHAR(512) NOT NULL,   -- plaintext category name or its (longer) deterministic ciphertext
    CONSTRAINT FK_ImageBookmarkCategory_Bookmark FOREIGN KEY (ImageBookmarkId) REFERENCES dbo.ImageBookmark(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_ImageBookmarkCategory UNIQUE (ImageBookmarkId, Category)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ImageBookmarkCategory_Bookmark')
CREATE INDEX IX_ImageBookmarkCategory_Bookmark ON dbo.ImageBookmarkCategory (ImageBookmarkId);
GO

-- Per-user, per-model banned tags/artists. Excluded from auto-gen (random prompt/artist) for that model
-- only; never affects manual generation or inference conditioning. Name is canonical (lowercase, underscored).
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'BannedToken' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.BannedToken
(
    Id         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BannedToken PRIMARY KEY,
    UserId     BIGINT        NOT NULL,
    ModelId    NVARCHAR(128) NOT NULL,
    Name       NVARCHAR(512) NOT NULL,   -- plaintext name or its (longer) deterministic ciphertext
    Kind       TINYINT       NOT NULL,
    SavedAtUtc DATETIME2(3)  NOT NULL,
    CONSTRAINT FK_BannedToken_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_BannedToken_User_Model_Name_Kind UNIQUE (UserId, ModelId, Name, Kind)
);
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Name' AND Object_ID = Object_ID('dbo.BannedToken') AND max_length = 512)
    ALTER TABLE dbo.BannedToken ALTER COLUMN Name NVARCHAR(512) NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BannedToken_User_Model')
CREATE INDEX IX_BannedToken_User_Model ON dbo.BannedToken (UserId, ModelId);
GO

-- Jobs handed to ForgeGateway whose result hasn't been written to history yet. The server-side reconciler
-- polls the gateway for these and records the final HistoryEntry, so a generation persists even when the
-- originating browser closes (or is on another device). Cleared once recorded / failed / aged out.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PendingJob' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.PendingJob
(
    Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PendingJob PRIMARY KEY,
    UserId        BIGINT         NOT NULL,
    JobId         NVARCHAR(128)  NOT NULL,
    Prompt        NVARCHAR(MAX)  NOT NULL,
    ModelFriendly NVARCHAR(256)  NOT NULL,
    ModelId       NVARCHAR(128)  NOT NULL,
    Aspect        NVARCHAR(16)   NOT NULL,
    CreatedAtUtc  DATETIME2(3)   NOT NULL,
    CONSTRAINT FK_PendingJob_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_PendingJob_User_Job UNIQUE (UserId, JobId)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PendingJob_Created')
CREATE INDEX IX_PendingJob_Created ON dbo.PendingJob (CreatedAtUtc ASC, Id ASC);
GO

-- A user's chosen display image for an artist (what represents the artist on the bookmarks/artist pages).
-- Per-user; when absent the artist falls back to the user's most recent generation for it. ArtistName is the
-- canonical token (lowercase, underscored).
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ArtistDisplay' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.ArtistDisplay
(
    Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ArtistDisplay PRIMARY KEY,
    UserId         BIGINT         NOT NULL,
    ArtistName     NVARCHAR(512)  NOT NULL,   -- plaintext artist token or its (longer) deterministic ciphertext
    GatewayImageId NVARCHAR(256)  NOT NULL,
    SetAtUtc       DATETIME2(3)   NOT NULL,
    CONSTRAINT FK_ArtistDisplay_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_ArtistDisplay_User_Artist UNIQUE (UserId, ArtistName)
);
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'ArtistName' AND Object_ID = Object_ID('dbo.ArtistDisplay') AND max_length = 512)
    ALTER TABLE dbo.ArtistDisplay ALTER COLUMN ArtistName NVARCHAR(512) NOT NULL;
GO

-- A user's chosen cover image for a LoRA (the picker grid). Mirrors dbo.ArtistDisplay: per-user, references one of
-- the user's own generations by id, LoraName deterministically encrypted. LoraName is the subfolder-qualified
-- lora_name exactly as ComfyUI reports it.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LoraDisplay' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.LoraDisplay
(
    Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LoraDisplay PRIMARY KEY,
    UserId         BIGINT         NOT NULL,
    LoraName       NVARCHAR(512)  NOT NULL,   -- subfolder-qualified lora_name's deterministic ciphertext
    GatewayImageId NVARCHAR(256)  NOT NULL,
    SetAtUtc       DATETIME2(3)   NOT NULL,
    CONSTRAINT FK_LoraDisplay_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_LoraDisplay_User_Lora UNIQUE (UserId, LoraName)
);
GO

-- A user's chosen portrait image for a tag (the bookmarks page). Mirrors dbo.ArtistDisplay/LoraDisplay.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TagDisplay' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.TagDisplay
(
    Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TagDisplay PRIMARY KEY,
    UserId         BIGINT         NOT NULL,
    TagName        NVARCHAR(512)  NOT NULL,   -- canonical tag token's deterministic ciphertext
    GatewayImageId NVARCHAR(256)  NOT NULL,
    SetAtUtc       DATETIME2(3)   NOT NULL,
    CONSTRAINT FK_TagDisplay_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_TagDisplay_User_Tag UNIQUE (UserId, TagName)
);
GO

-- Machine-level cache of what CivitAI knows about a LoRA file (looked up by hash). Not per-user; LoraName is the
-- plain subfolder-qualified filename (a shared machine asset, like dbo.ModelBinding.FileName).
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LoraMeta' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.LoraMeta
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LoraMeta PRIMARY KEY,
    LoraName     NVARCHAR(512)  NOT NULL,
    Sha256       NVARCHAR(64)   NULL,
    TrainedWords NVARCHAR(MAX)  NULL,   -- JSON array of CivitAI trigger words (may be [])
    ModelName    NVARCHAR(256)  NULL,
    PreviewUrl   NVARCHAR(1024) NULL,
    FetchedAtUtc DATETIME2(3)   NOT NULL,
    CONSTRAINT UQ_LoraMeta_Name UNIQUE (LoraName)
);
GO

-- Machine-level cache of a LoRA's CivitAI preview media (an image, or a short clip — some previews are mp4). Downloaded
-- once and served from this box rather than hotlinking the CivitAI CDN. Keyed by the plain filename, like dbo.LoraMeta.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LoraPreview' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.LoraPreview
(
    LoraName     NVARCHAR(512)  NOT NULL CONSTRAINT PK_LoraPreview PRIMARY KEY,
    Bytes        VARBINARY(MAX) NOT NULL,
    ContentType  NVARCHAR(64)   NOT NULL,
    FetchedAtUtc DATETIME2(3)   NOT NULL
);
GO

-- Per-user LoRA preferences: a trigger-word override (NULL = use the CivitAI default) and whether to auto-attach
-- the trigger words to the prompt. LoraName is deterministically encrypted, like dbo.LoraDisplay.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LoraUserSetting' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.LoraUserSetting
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LoraUserSetting PRIMARY KEY,
    UserId       BIGINT         NOT NULL,
    LoraName     NVARCHAR(512)  NOT NULL,
    TriggerWords NVARCHAR(MAX)  NULL,
    AutoAttach   BIT            NOT NULL CONSTRAINT DF_LoraUserSetting_AutoAttach DEFAULT 1,
    CONSTRAINT FK_LoraUserSetting_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_LoraUserSetting_User_Lora UNIQUE (UserId, LoraName)
);
GO

-- Durable image storage. Image bytes live here keyed by a globally-unique opaque id (a GUID), replacing the
-- old scheme where a GatewayImageId was a ComfyUI view-ref served by proxy -- which collided when ComfyUI's
-- per-prefix filename counter reset (the app and the MCP submit under the same prefix to one ComfyUI) and
-- vanished when its output dir rotated. HistoryEntry/ImageBookmark/ArtistDisplay reference these ids by string;
-- images are served DB-first with a ComfyUI /view fallback for any id not yet backfilled.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ImageBlob' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.ImageBlob
(
    ImageId      NVARCHAR(64)   NOT NULL CONSTRAINT PK_ImageBlob PRIMARY KEY,
    Bytes        VARBINARY(MAX) NOT NULL,
    ContentType  NVARCHAR(64)   NOT NULL CONSTRAINT DF_ImageBlob_ContentType DEFAULT 'image/png',
    Width        INT            NULL,
    Height       INT            NULL,
    ByteSize     INT            NOT NULL,
    Kind         TINYINT        NOT NULL CONSTRAINT DF_ImageBlob_Kind DEFAULT 0,   -- 0=generated (1=upload: historical, no longer written)
    CreatedAtUtc DATETIME2(3)   NOT NULL CONSTRAINT DF_ImageBlob_Created DEFAULT SYSUTCDATETIME()
);
GO

-- Sprite pixelizer extras: a pixel-quantize generation's DERIVED palette (JSON array of #RRGGBB), stored alongside
-- the produced image so the sprite pipeline can snap to the TRUE colours instead of re-deriving from the lossy webp.
-- Nullable; only pixel-quantize (fp) generations set it. Idempotent add for already-created databases.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ImageBlob') AND name = 'PaletteJson')
    ALTER TABLE dbo.ImageBlob ADD PaletteJson NVARCHAR(MAX) NULL;
GO

-- The fp quantize's pooled label FREQUENCIES (JSON float array, indexed by PaletteJson order) — the second
-- batch-global (besides the palette) the fp engine's rarity weighting depends on. Persisted so a later
-- single-frame re-quantize can replay BOTH globals and reproduce the whole-batch result exactly.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ImageBlob') AND name = 'FrequenciesJson')
    ALTER TABLE dbo.ImageBlob ADD FrequenciesJson NVARCHAR(MAX) NULL;
GO

-- Native-resolution LOSSLESS frames of a pixel-art clip: one PNG per frame at the block-grid resolution, captured
-- BEFORE the lossy webp encode, so the sprite pipeline can request clean frames (no compression artifacts, no
-- downscale needed) rather than decoding the lossy animated webp. Keyed to the produced ImageBlob id.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ImageFrame' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.ImageFrame
(
    ImageId    NVARCHAR(64)   NOT NULL,
    FrameIndex INT            NOT NULL,
    Bytes      VARBINARY(MAX) NOT NULL,
    CONSTRAINT PK_ImageFrame PRIMARY KEY (ImageId, FrameIndex)
);
GO

-- Per-generation render timing. One row per SUCCESSFUL gen/edit, holding the actual ComfyUI render time
-- (DurationMs = submit -> image ready, so it EXCLUDES the fair-queue wait). Recorded per MachineName because
-- 204 and 206 render at different speeds; the UI's ETA for a model is the average of its last few records on the
-- machine doing the work (null the first time a model runs there). Insert/select only -- the app login needs no
-- DDL; this table is created out-of-band (setup-database.ps1 / an elevated sqlcmd) like the rest of the schema.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GenTiming' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.GenTiming
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_GenTiming PRIMARY KEY,
    MachineName  NVARCHAR(128)  NOT NULL,
    ConfigId     NVARCHAR(128)  NOT NULL,   -- the workflow configuration id (the 'model' submitted)
    IsEdit       BIT            NOT NULL,
    DurationMs   INT            NOT NULL,    -- ComfyUI submit -> image ready (queue wait excluded)
    CreatedAtUtc DATETIME2(3)   NOT NULL CONSTRAINT DF_GenTiming_Created DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_GenTiming_Machine_Config')
CREATE INDEX IX_GenTiming_Machine_Config ON dbo.GenTiming (MachineName ASC, ConfigId ASC, Id DESC);
GO

-- Parameter-constrained ETA: the merged render params that drive gen time, captured with each timing sample so the ETA
-- is matched to the request (resolution × steps × frames) instead of a flat per-model average. Nullable; pre-existing
-- rows and workflows that mark no EtaVariable params leave them NULL and fall back to the per-model average.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = 'RenderWidth' AND object_id = OBJECT_ID('dbo.GenTiming'))
    ALTER TABLE dbo.GenTiming ADD RenderWidth INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = 'RenderHeight' AND object_id = OBJECT_ID('dbo.GenTiming'))
    ALTER TABLE dbo.GenTiming ADD RenderHeight INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = 'Steps' AND object_id = OBJECT_ID('dbo.GenTiming'))
    ALTER TABLE dbo.GenTiming ADD Steps INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE name = 'Frames' AND object_id = OBJECT_ID('dbo.GenTiming'))
    ALTER TABLE dbo.GenTiming ADD Frames INT NULL;
GO

-- A render job: the durable, write-through home of what was once purely in-memory JobQueue state. One job owns N
-- ordered slots (one slot = one image = one ComfyUI prompt); a lone /generate or /edit is a 1-slot job, a batch is
-- an N-slot job. The job is a LIVE PROJECTION OF COMFYUI'S STATE: the owning instance reconciles each outstanding
-- slot against ComfyUI on every read/worker tick, advances Progress, and FINALIZES (Status=Done/Error, FinishedAtUtc
-- set) once every slot is terminal -- at which point it stops appearing in the active /forge/jobs feed and the client
-- treats its disappearance as the cue to reconcile from history. MachineName is the OWNING instance: only that
-- instance reconciles/advances/finalizes the job (each instance drives its own GPU/ComfyUI; invariant #4). Other
-- instances may READ a finalized job by id (durable), never act on another instance's live job.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Job' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.Job
(
    JobId         NVARCHAR(64)   NOT NULL CONSTRAINT PK_Job PRIMARY KEY,   -- our GUID; the public job handle
    UserId        BIGINT         NOT NULL,
    MachineName   NVARCHAR(128)  NOT NULL,                                 -- owning instance (only it reconciles)
    Model         NVARCHAR(128)  NOT NULL,                                 -- display: the job's configuration id
    Prompt        NVARCHAR(MAX)  NOT NULL,                                 -- display: the job's prompt/instruction
    Total         INT            NOT NULL,                                 -- slot count (images this job will make)
    Status        TINYINT        NOT NULL,                                 -- 0=Active, 1=Done, 2=Error, 3=Cancelled (job-level)
    CreatedAtUtc  DATETIME2(3)   NOT NULL,
    FinishedAtUtc DATETIME2(3)   NULL,                                     -- set when all slots terminal (finalized)
    CONSTRAINT FK_Job_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE
);
GO

-- This instance's still-active jobs (rehydrated on startup so an app restart resumes them); and the per-user active
-- feed. Status 0 = Active.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Job_Machine_Status')
CREATE INDEX IX_Job_Machine_Status ON dbo.Job (MachineName, Status, CreatedAtUtc ASC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Job_User_Status_Created')
CREATE INDEX IX_Job_User_Status_Created ON dbo.Job (UserId, Status, CreatedAtUtc ASC, JobId ASC);
GO

-- One image slot of a job. State advances Queued->Running->Done|Error as the worker submits the slot's ComfyUI
-- prompt and the result lands (or ComfyUI loses the prompt). ImageId is the produced image's durable id (dbo.ImageBlob);
-- it fills the job's positional id-array the client diffs to know "a new image exists, go fetch it". RequestJson was the
-- serialized GenerateRequest/EditRequest so the worker can (re)render the slot -- including after an app restart, where
-- an as-yet-unsubmitted slot is re-queued. MarksJson/EffectivePrompt carry what's needed to write the HistoryEntry.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'JobSlot' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.JobSlot
(
    Id                 BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_JobSlot PRIMARY KEY,
    JobId              NVARCHAR(64)  NOT NULL,
    SlotIndex          INT           NOT NULL,                  -- position in the job's image array
    IsEdit             BIT           NOT NULL,
    State              TINYINT       NOT NULL,                  -- 0=Queued, 1=Running, 2=Done, 3=Error, 4=Cancelled
    ComfyPromptId      NVARCHAR(128) NULL,                      -- ComfyUI prompt id (internal; liveness key)
    ImageId            NVARCHAR(64)  NULL,                      -- produced image (dbo.ImageBlob id)
    Width              INT           NULL,
    Height             INT           NULL,
    Changed            BIT           NOT NULL CONSTRAINT DF_JobSlot_Changed DEFAULT 1,   -- edits: false = model declined
    ChangeScore        FLOAT         NULL,                      -- edits only (pHash distance)
    Error              NVARCHAR(MAX) NULL,
    EffectivePrompt    NVARCHAR(MAX) NULL,                      -- prompt actually rendered (markers/random handled)
    RawPrompt          NVARCHAR(MAX) NULL,                      -- prompt VERBATIM in marker form, random injections included
    RawNegativePrompt  NVARCHAR(MAX) NULL,                      -- negative VERBATIM in marker form
    MarksJson          NVARCHAR(MAX) NULL,                      -- SUPERSEDED by dbo.JobSlotMark (kept: this file never drops data)
    RequestJson        NVARCHAR(MAX) NULL,                      -- SUPERSEDED by the typed spec columns below (kept: never drops data)
    GenStartedAtUtc    DATETIME2(3)  NULL,                      -- when the render started (excludes queue wait)
    ExpectedGenSeconds FLOAT         NULL,                      -- ETA seed (machine+model recent average)
    CONSTRAINT FK_JobSlot_Job FOREIGN KEY (JobId) REFERENCES dbo.Job(JobId) ON DELETE CASCADE,
    CONSTRAINT UQ_JobSlot_Job_Index UNIQUE (JobId, SlotIndex)
);
GO

-- The slot carries the raw prompt (and negative) so a job resumed after a restart still writes them to history.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'RawPrompt' AND Object_ID = Object_ID('dbo.JobSlot'))
    ALTER TABLE dbo.JobSlot ADD RawPrompt NVARCHAR(MAX) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'RawNegativePrompt' AND Object_ID = Object_ID('dbo.JobSlot'))
    ALTER TABLE dbo.JobSlot ADD RawNegativePrompt NVARCHAR(MAX) NULL;
GO

-- The slot's render SPEC, as typed columns. It used to be one encrypted JSON blob (RequestJson) holding eleven
-- fields because two of them are protected, which dragged four image FOREIGN KEYS behind an opaque wall: nothing
-- could join them, count them, or garbage-collect against them. That is not a hypothetical -- it is exactly how
-- 19,329 upload rows / 7.1 GB became unreachable, their only reference living inside this blob.
--
-- Encryption is per FIELD now. Prompt and NegativePrompt are user text and stay randomized-encrypted; ids, flags and
-- numbers are plain so the database can query and cascade on them. TagTypes/Overrides stay JSON deliberately: they
-- are value bags (a set of type names, an arbitrary workflow parameter map), not relations to anything -- the same
-- treatment AppUser.GenerationTagTypes already gets.
--
-- Typed columns also delete a whole failure class: RequestJson was a serialization contract, and a renamed property
-- deserialized SILENTLY into a null, which is how one job sat Active for five weeks. A renamed column fails here,
-- loudly, instead of producing an object with a hole in it.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Workflow' AND Object_ID = Object_ID('dbo.JobSlot'))
    ALTER TABLE dbo.JobSlot ADD
        Workflow           NVARCHAR(128) NULL,   -- FK to a workflow configuration id (plain)
        Prompt             NVARCHAR(MAX) NULL,   -- generate prompt / edit instruction (ENCRYPTED)
        NegativePrompt     NVARCHAR(MAX) NULL,   -- (ENCRYPTED)
        Aspect             NVARCHAR(16)  NULL,
        RandomArtist       BIT           NULL,
        RandomPrompt       BIT           NULL,
        Temperature        FLOAT         NULL,
        TagTypesJson       NVARCHAR(256) NULL,   -- value set, like AppUser.GenerationTagTypes (plain)
        OverridesJson      NVARCHAR(MAX) NULL,   -- arbitrary workflow parameter map (plain)
        SourceImageId      NVARCHAR(64)  NULL,   -- edits: the source image (plain, joinable)
        MaskImageId        NVARCHAR(64)  NULL,   -- inpaint mask (plain, joinable)
        LastFrameImageId   NVARCHAR(64)  NULL;   -- i2v end frame (plain, joinable)
GO

-- The user's LoRA stack for the slot: [{name,weight}] as JSON. A value bag, not a relation — the same plain,
-- per-slot treatment OverridesJson gets — so a batch resumed after a restart re-renders with its LoRAs intact.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'LorasJson' AND Object_ID = Object_ID('dbo.JobSlot'))
    ALTER TABLE dbo.JobSlot ADD LorasJson NVARCHAR(MAX) NULL;
GO

-- Background (idle-time) slots: a slot marked background runs only once the queue has been foreground-idle for the
-- configured delay, and a foreground submission preempts it (halting and requeuing it, never failing it). Plain, not
-- protected — it names scheduling policy, not user content. NOT NULL with a constant default so existing rows adopt
-- "foreground" without a backfill and anything already queued keeps running as before.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'IsBackground' AND Object_ID = Object_ID('dbo.JobSlot'))
    ALTER TABLE dbo.JobSlot ADD IsBackground BIT NOT NULL CONSTRAINT DF_JobSlot_IsBackground DEFAULT 0;
GO

-- The edit's reference images: an ordered many-to-many, so a real child table rather than an array inside a blob.
-- Ordinal is load-bearing -- reference images are positional to the workflow.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'JobSlotReference' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.JobSlotReference
(
    JobId     NVARCHAR(64) NOT NULL,
    SlotIndex INT          NOT NULL,
    Ordinal   INT          NOT NULL,
    ImageId   NVARCHAR(64) NOT NULL,
    CONSTRAINT PK_JobSlotReference PRIMARY KEY (JobId, SlotIndex, Ordinal),
    CONSTRAINT FK_JobSlotReference_Slot FOREIGN KEY (JobId, SlotIndex)
        REFERENCES dbo.JobSlot(JobId, SlotIndex) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSlotReference_Image')
CREATE INDEX IX_JobSlotReference_Image ON dbo.JobSlotReference (ImageId);
GO

-- The produced image's marks. Mirrors dbo.HistoryMark exactly -- the pattern that already works -- instead of the
-- encrypted { token -> "tag"|"artist" } blob this replaces, which was the one copy of this data nothing could query.
-- Token is DETERMINISTICALLY encrypted, so equality, IN (...) and UNIQUE all still work over it.
-- A SURROGATE key, exactly as dbo.HistoryMark has: the deterministic ciphertext of a token is NVARCHAR(512), and
-- a clustered key containing it exceeds SQL Server's 900-byte limit -- which CREATEs with only a warning and then
-- fails the INSERT for a long token. Uniqueness is enforced by the writer's NOT EXISTS guard instead.
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'JobSlotMark' AND schema_id = SCHEMA_ID('dbo'))
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Id' AND Object_ID = Object_ID('dbo.JobSlotMark'))
    -- The emptiness check lives in EXEC: an IF condition is not guaranteed to short-circuit, so naming the
    -- table directly here fails to resolve on a database that never had it.
    EXEC('IF NOT EXISTS (SELECT 1 FROM dbo.JobSlotMark) DROP TABLE dbo.JobSlotMark;');
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'JobSlotMark' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.JobSlotMark
(
    Id        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_JobSlotMark PRIMARY KEY,
    JobId     NVARCHAR(64)  NOT NULL,
    SlotIndex INT           NOT NULL,
    Token     NVARCHAR(512) NOT NULL,   -- deterministic ciphertext of the canonical token
    Kind      TINYINT       NOT NULL,
    CONSTRAINT FK_JobSlotMark_Slot FOREIGN KEY (JobId, SlotIndex)
        REFERENCES dbo.JobSlot(JobId, SlotIndex) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSlotMark_Slot')
CREATE INDEX IX_JobSlotMark_Slot ON dbo.JobSlotMark (JobId, SlotIndex);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSlotMark_Token')
CREATE INDEX IX_JobSlotMark_Token ON dbo.JobSlotMark (Token, Kind);
GO

-- The user's workflow relations, as relations. Favourites and hidden workflows are user x workflow and custom
-- workflow tags are user x workflow x tag; all three were JSON blobs on AppUser, so nothing could ask which users
-- favourited a workflow and nothing cleaned up when one left the catalog. Workflow ids are not sensitive and stay
-- plain; a custom TAG is the user's own label, so it is deterministically encrypted (it has to stay unique per pair).
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UserFavoriteWorkflow' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.UserFavoriteWorkflow
(
    UserId     BIGINT        NOT NULL,
    WorkflowId NVARCHAR(128) NOT NULL,
    CONSTRAINT PK_UserFavoriteWorkflow PRIMARY KEY (UserId, WorkflowId),
    CONSTRAINT FK_UserFavoriteWorkflow_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UserHiddenWorkflow' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.UserHiddenWorkflow
(
    UserId     BIGINT        NOT NULL,
    WorkflowId NVARCHAR(128) NOT NULL,
    CONSTRAINT PK_UserHiddenWorkflow PRIMARY KEY (UserId, WorkflowId),
    CONSTRAINT FK_UserHiddenWorkflow_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE
);
GO

-- Hidden from the API workflow list, independent of the UI picker: a user can keep a workflow in their
-- picker but out of what their API key returns, or vice versa. Mirrors dbo.UserHiddenWorkflow.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UserHiddenApiWorkflow' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.UserHiddenApiWorkflow
(
    UserId     BIGINT        NOT NULL,
    WorkflowId NVARCHAR(128) NOT NULL,
    CONSTRAINT PK_UserHiddenApiWorkflow PRIMARY KEY (UserId, WorkflowId),
    CONSTRAINT FK_UserHiddenApiWorkflow_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE
);
GO

-- Surrogate key for the same reason as dbo.JobSlotMark: the encrypted Tag is too wide to sit in a clustered key.
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UserWorkflowTag' AND schema_id = SCHEMA_ID('dbo'))
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Id' AND Object_ID = Object_ID('dbo.UserWorkflowTag'))
    -- The emptiness check lives in EXEC: an IF condition is not guaranteed to short-circuit, so naming the
    -- table directly here fails to resolve on a database that never had it.
    EXEC('IF NOT EXISTS (SELECT 1 FROM dbo.UserWorkflowTag) DROP TABLE dbo.UserWorkflowTag;');
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UserWorkflowTag' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.UserWorkflowTag
(
    Id         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserWorkflowTag PRIMARY KEY,
    UserId     BIGINT        NOT NULL,
    WorkflowId NVARCHAR(128) NOT NULL,
    Tag        NVARCHAR(512) NOT NULL,   -- deterministic ciphertext of the user's own label
    CONSTRAINT FK_UserWorkflowTag_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_UserWorkflowTag_User')
CREATE INDEX IX_UserWorkflowTag_User ON dbo.UserWorkflowTag (UserId, WorkflowId);
GO

-- Tags are a DEFINITION property (the card's tags) overridden by a per-workflow delta: Removed = 0 is a tag the user
-- ADDED on top of the base tags (every pre-existing row, so no backfill), Removed = 1 is a BASE tag they took off.
-- The displayed set is (base + added) minus removed, computed client-side. Guarded ALTER, constant default -- 0 =
-- "added", so existing rows keep their meaning.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Removed' AND Object_ID = Object_ID('dbo.UserWorkflowTag'))
    ALTER TABLE dbo.UserWorkflowTag ADD Removed BIT NOT NULL CONSTRAINT DF_UserWorkflowTag_Removed DEFAULT 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSlot_Job')
CREATE INDEX IX_JobSlot_Job ON dbo.JobSlot (JobId, SlotIndex ASC);
GO

-- Per-user encrypted application log. A private, auditable trail of prompt-bearing events (e.g. the random-prompt
-- predictor in/out, the prompt actually submitted) that would otherwise leak in plaintext to the console/app log.
-- Payload is randomized AES-GCM ciphertext (enc:v1:...) under the owning user's key, so it reads as opaque text in
-- SSMS. Writes are gated by Logging:AuditUserPrompts (default off). Category is a short, non-sensitive event label.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UserLog' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.UserLog
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserLog PRIMARY KEY,
    UserId       BIGINT        NOT NULL,
    Category     NVARCHAR(64)  NOT NULL,
    Payload      NVARCHAR(MAX) NOT NULL,
    CreatedAtUtc DATETIME2(3)  NOT NULL,
    CONSTRAINT FK_UserLog_User FOREIGN KEY (UserId) REFERENCES dbo.AppUser(Id) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_UserLog_User_Created')
CREATE INDEX IX_UserLog_User_Created ON dbo.UserLog (UserId, CreatedAtUtc DESC, Id DESC);
GO

-- ============================================================================================================
-- Install-wide catalogue overrides. Keyed by MachineName, like GenTiming and Job: these describe THIS BOX --
-- which file is on its disk, what its GPU can afford -- not a user's preference, so they are deliberately not
-- per-user and carry no encryption. There is no role system; any authenticated user may edit them, which is
-- consistent with an app whose defaults assume a machine you trust.
-- ============================================================================================================

-- Which file on this machine fills a catalogue slot. Replaces the shipped filename that used to live in
-- requirements.json, where it was one person's disk and had to match EXACTLY or the workflow silently vanished.
-- IsAuto records that a match pattern chose this rather than a person: an auto binding may be re-evaluated when
-- the catalogue improves, a hand-picked one never is.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ModelBinding' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.ModelBinding
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ModelBinding PRIMARY KEY,
    MachineName  NVARCHAR(128) NOT NULL,
    SlotId       NVARCHAR(128) NOT NULL,   -- configurations/models/<id>.json
    FileName     NVARCHAR(512) NOT NULL,   -- as ComfyUI reports it
    IsAuto       BIT           NOT NULL CONSTRAINT DF_ModelBinding_IsAuto DEFAULT 0,
    UpdatedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_ModelBinding_Updated DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ModelBinding_Machine_Slot')
CREATE UNIQUE INDEX UX_ModelBinding_Machine_Slot ON dbo.ModelBinding (MachineName, SlotId);
GO

-- Per-configuration setting overrides for this machine: VRAM floor/ceiling and exposed-parameter defaults
-- (aspect, size, steps...). One generic key/value pair rather than a column per setting, because the set of
-- things worth overriding is open-ended and a column per setting means a migration per idea. SettingKey is
-- namespaced -- 'vram.min', 'vram.max', 'param.<key>' -- and SettingValue is the raw text, coerced through the
-- parameter's existing ParamSpec type.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ConfigOverride' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.ConfigOverride
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConfigOverride PRIMARY KEY,
    MachineName  NVARCHAR(128) NOT NULL,
    ConfigId     NVARCHAR(128) NOT NULL,
    SettingKey   NVARCHAR(128) NOT NULL,
    SettingValue NVARCHAR(MAX) NOT NULL,
    UpdatedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_ConfigOverride_Updated DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ConfigOverride_Machine_Config_Key')
CREATE UNIQUE INDEX UX_ConfigOverride_Machine_Config_Key ON dbo.ConfigOverride (MachineName, ConfigId, SettingKey);
GO

-- This machine's own configuration -- the keys that used to live in appsettings.json. Per-MACHINE for the same
-- reason ModelBinding is: one database can back several app instances, and the renderer's address is a property
-- of the box, not of the install. A key lives HERE or in the file, never both; the file keeps only what is needed
-- to open this database (see MachineSettingsConfigurationSource). SettingKey is the configuration path exactly as
-- IConfiguration spells it -- 'ComfyUI:BaseUrl' -- so the stored rows ARE the configuration section they name.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'MachineSetting' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.MachineSetting
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MachineSetting PRIMARY KEY,
    MachineName  NVARCHAR(128) NOT NULL,
    SettingKey   NVARCHAR(256) NOT NULL,
    SettingValue NVARCHAR(MAX) NOT NULL,
    UpdatedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_MachineSetting_Updated DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_MachineSetting_Machine_Key')
CREATE UNIQUE INDEX UX_MachineSetting_Machine_Key ON dbo.MachineSetting (MachineName, SettingKey);
GO

-- DB-backed workflow variants: a duplicate of a shipped configuration held as a coexisting, independently selectable
-- catalogue entry (e.g. a hi-res and a low-res version of one model). Per-MACHINE, like ModelBinding/ConfigOverride:
-- a variant is a property of this box's catalogue, and the shipped files are immutable. ParamsJson is a SNAPSHOT of the
-- base's effective params at copy time; later per-variant tweaks ride dbo.ConfigOverride keyed on VariantId.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkflowVariant' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.WorkflowVariant
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WorkflowVariant PRIMARY KEY,
    MachineName  NVARCHAR(128) NOT NULL,
    VariantId    NVARCHAR(128) NOT NULL,   -- the variant's own catalogue id (what the client sends as 'model')
    BaseConfigId NVARCHAR(128) NOT NULL,   -- the shipped configuration it was duplicated from
    FriendlyName NVARCHAR(256) NOT NULL,
    ParamsJson   NVARCHAR(MAX) NOT NULL,   -- snapshot of the base's effective params { key: value } at copy time
    CreatedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_WorkflowVariant_Created DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_WorkflowVariant_Machine_Variant')
CREATE UNIQUE INDEX UX_WorkflowVariant_Machine_Variant ON dbo.WorkflowVariant (MachineName, VariantId);
GO

-- Mark PROVENANCE: 1 when a random sampler (random-prompt tag or random-artist) APPENDED the token, 0 when the user
-- typed it. The viewer dashes the border of generated chips. New column on the three pre-existing mark tables, so it
-- MUST be a guarded ALTER (an existing database skips the CREATEs above). NOT NULL with a constant default -- 0 = "not
-- known to be generated", so pre-provenance rows render no dash (never a guess) without a backfill.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Generated' AND Object_ID = Object_ID('dbo.HistoryMark'))
    ALTER TABLE dbo.HistoryMark ADD Generated BIT NOT NULL CONSTRAINT DF_HistoryMark_Generated DEFAULT 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Generated' AND Object_ID = Object_ID('dbo.ImageBookmarkMark'))
    ALTER TABLE dbo.ImageBookmarkMark ADD Generated BIT NOT NULL CONSTRAINT DF_ImageBookmarkMark_Generated DEFAULT 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'Generated' AND Object_ID = Object_ID('dbo.JobSlotMark'))
    ALTER TABLE dbo.JobSlotMark ADD Generated BIT NOT NULL CONSTRAINT DF_JobSlotMark_Generated DEFAULT 0;
GO

-- Server-side auth sessions, moved out of the in-process MemoryTicketStore so a signed-in session survives an app
-- restart. The cookie still carries only the opaque SessionKey; this row holds the serialized ticket. The ghost-cookie
-- guarantee is preserved -- wiping the database wipes these rows, so a surviving cookie names no session and the
-- request is simply anonymous. Expired rows are swept on sign-in and filtered on read.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AuthSession' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.AuthSession
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuthSession PRIMARY KEY,
    SessionKey   NVARCHAR(64)   NOT NULL,
    Ticket       VARBINARY(MAX) NOT NULL,
    ExpiresAtUtc DATETIME2(3)   NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_AuthSession_SessionKey')
CREATE UNIQUE INDEX UX_AuthSession_SessionKey ON dbo.AuthSession (SessionKey);
GO

-- The ASP.NET Data Protection key ring, moved out of the OS user profile so the keys that unprotect the auth cookie
-- live and die with the accounts and sessions they protect (and follow the database to another box). Append-only:
-- the key manager only ever adds keys and reads them all back in insertion (Id) order.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'DataProtectionKey' AND schema_id = SCHEMA_ID('dbo'))
CREATE TABLE dbo.DataProtectionKey
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DataProtectionKey PRIMARY KEY,
    FriendlyName NVARCHAR(256) NOT NULL,
    Xml          NVARCHAR(MAX) NOT NULL,
    CreatedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_DataProtectionKey_Created DEFAULT SYSUTCDATETIME()
);
GO

-- The image visibility check resolves an image id to its owner through dbo.JobSlot.ImageId (joined to dbo.Job), and
-- runs on every image/thumbnail/clip request. Without this index that lookup is a scan of every slot on the box.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_JobSlot_Image')
CREATE INDEX IX_JobSlot_Image ON dbo.JobSlot (ImageId);
GO

-- Exact positive prompt embedded in the submitted workflow graph after prompt-template rendering. Kept apart from
-- EffectivePrompt because the established image display shows the concise prompt while Generation Values shows what
-- the model actually received. User content: randomized-encrypted by the repository. Existing rows remain null.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'ModelPrompt' AND Object_ID = Object_ID('dbo.JobSlot'))
    ALTER TABLE dbo.JobSlot ADD ModelPrompt NVARCHAR(MAX) NULL;
GO

-- Model-file/loader snapshot resolved when a render is submitted. Plain operational metadata; existing rows remain
-- null, and later ModelBinding edits cannot rewrite the weights recorded for an existing image.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'ModelManifestJson' AND Object_ID = Object_ID('dbo.JobSlot'))
    ALTER TABLE dbo.JobSlot ADD ModelManifestJson NVARCHAR(MAX) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'RenderDimensionsJson' AND Object_ID = Object_ID('dbo.JobSlot'))
    ALTER TABLE dbo.JobSlot ADD RenderDimensionsJson NVARCHAR(MAX) NULL;
GO
