-- ImageGen schema, SQLite.  ***APPEND-ONLY. VERSION-SEGREGATED. NOTHING BELOW IS EVER MODIFIED.***
--
-- ================================================================================================================
--  HOW THIS FILE WORKS -- READ THIS BEFORE YOU TOUCH IT
-- ================================================================================================================
--  This is NOT a snapshot of the current shape. It is the ORDERED HISTORY of every schema change, split into one
--  block per released version, and DatabaseInitializer REPLAYS IT IN FULL, top to bottom, on every startup against
--  whatever database the user already has -- a fresh file, a 0.9 file, a 0.10 file, any of them.
--
--  The rules are not optional. An existing user's database is upgraded IN PLACE by re-running this file:
--
--    1. NEVER MODIFY A RELEASED BLOCK. Not a column, not a type, not a constraint, not a comment's meaning. The
--       0.9.0 block is frozen forever, and so is every block once its version has shipped. Editing a released
--       block does NOTHING to a database that already ran it (see rule 3) -- it only misleads the next reader
--       about what that version actually shipped.
--
--    2. A NEW VERSION APPENDS A NEW BLOCK AT THE BOTTOM. Add a `-- --- X.Y.Z` banner and put that version's
--       changes beneath it. That is the ONLY way the schema changes. Every change is therefore purely additive
--       and attributable to the exact version that introduced it.
--
--    3. EVERY STATEMENT MUST BE IDEMPOTENT, because the whole file re-runs on every startup:
--         * new TABLE  -> CREATE TABLE IF NOT EXISTS
--         * new INDEX  -> CREATE [UNIQUE] INDEX IF NOT EXISTS
--         * new COLUMN on an already-existing table -> ALTER TABLE dbo.<Table> ADD COLUMN <Name> ...
--             SQLite has no `ADD COLUMN IF NOT EXISTS`, so DatabaseInitializer runs an ADD COLUMN only when the
--             column is absent (it checks PRAGMA table_info first). Write the plain ALTER; the runner makes it a
--             no-op on replay. DO NOT inline a new column into an older block's CREATE TABLE: that CREATE is
--             skipped on every existing database (the table is already there), so the column would never arrive.
--             That exact mistake shipped `JobSlot.LorasJson` broken; it is now an ALTER in the 0.9.1 block below.
--
--    4. SQLite CANNOT alter, drop, or rename a column. A change that is not expressible as "add a table / add an
--       index / add a nullable column" is NOT a schema edit here -- stop and design a real table-rebuild
--       migration. Do not pretend an in-place edit is possible.
--
--  The SQL Server counterpart (schema.sql) carries the SAME history as guarded ALTERs in GO-separated batches;
--  this file is the SQLite expression of the same idea, one block per version. SqliteSchemaMigrationTests pins it.
-- ================================================================================================================
--
--  dbo. PREFIXES ARE REAL. SqliteConnectionFactory attaches the database file under the name `dbo`, which is what
--  lets ~130 hand-written statements elsewhere stay provider-agnostic. The DDL is NOT symmetric with DML, and
--  SqliteAttachSpikeTests pins both halves:
--    * CREATE TABLE dbo.X            -- schema-qualified, fine
--    * CREATE INDEX dbo.IX ON X      -- the INDEX name carries the schema; the TABLE must not
--    * REFERENCES X(Id)              -- a foreign key can never cross databases, so no prefix is permitted
--    * ALTER TABLE dbo.X ADD COLUMN  -- schema-qualified table, fine
--
--  TYPE MAPPING. NVARCHAR(n)/NVARCHAR(MAX)/DATETIME2(3) -> TEXT; BIGINT/INT/TINYINT/BIT -> INTEGER; FLOAT -> REAL;
--  VARBINARY -> BLOB. SQLite stores what it is given and converts on read, so the widths were never enforcement --
--  they were documentation, and they stay in schema.sql where they describe the real constraint.
--
--  Requires PRAGMA foreign_keys = ON (off by default) for the ON DELETE CASCADE below to do anything at all. The
--  connection factory sets it on every connection; the image-delete cascade depends on it.


-- --- 0.9.0 ------------------------------------------------------------------------------------------------------
-- First public release. Baseline: everything the schema contained up to and including 0.9.0. FROZEN -- see rule 1.

CREATE TABLE IF NOT EXISTS dbo.AppUser
(
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    -- COLLATE NOCASE is NOT cosmetic. On SQL Server the default collation is case-insensitive, so UQ_AppUser_Username
    -- already prevents 'Bob' and 'bob' both existing. SQLite compares case-SENSITIVELY by default, and losing that
    -- would let two accounts differ only by case -- with login then resolving to whichever the query happened to hit.
    Username     TEXT NOT NULL COLLATE NOCASE,
    PasswordHash TEXT NOT NULL,
    DisplayName  TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    -- Added over time on SQL Server; simply present here.
    ComposerPrefs       TEXT NULL,
    EditPrefs           TEXT NULL,
    FavoriteWorkflowIds TEXT NULL,   -- legacy blob, superseded by dbo.UserFavoriteWorkflow
    CustomWorkflowTags  TEXT NULL,   -- legacy blob, superseded by dbo.UserWorkflowTag
    HiddenWorkflowIds   TEXT NULL,   -- legacy blob, superseded by dbo.UserHiddenWorkflow
    GenerationTagTypes  TEXT NULL,
    BookmarkPrefs       TEXT NULL,
    ApiKey              TEXT NULL,
    CONSTRAINT UQ_AppUser_Username UNIQUE (Username)
);

-- Partial index: only rows that HAVE a key participate, so the many users without one do not all collide on NULL.
CREATE UNIQUE INDEX IF NOT EXISTS dbo.UQ_AppUser_ApiKey ON AppUser (ApiKey) WHERE ApiKey IS NOT NULL;

CREATE TABLE IF NOT EXISTS dbo.UserEncryptionKey
(
    UserId       INTEGER PRIMARY KEY,
    KeyMaterial  BLOB NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    CONSTRAINT FK_UserEncryptionKey_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS dbo.HistoryEntry
(
    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId            INTEGER NOT NULL,
    GatewayImageId    TEXT NOT NULL,
    Prompt            TEXT NOT NULL,   -- the FINALIZED prompt the model rendered (encrypted at rest)
    RawPrompt         TEXT NULL,       -- the prompt VERBATIM as submitted, in marker form (encrypted at rest)
    RawNegativePrompt TEXT NULL,       -- the negative VERBATIM as submitted, in marker form (encrypted at rest)
    OriginalPrompt    TEXT NULL,       -- the prompt as the user TYPED it, pre-expansion (encrypted at rest)
    ModelFriendly     TEXT NOT NULL,
    ModelId           TEXT NOT NULL,
    Aspect            TEXT NOT NULL,
    CreatedAtUtc      TEXT NOT NULL,
    CONSTRAINT FK_HistoryEntry_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_HistoryEntry_User_Image UNIQUE (UserId, GatewayImageId)
);
CREATE INDEX IF NOT EXISTS dbo.IX_HistoryEntry_User_Created
    ON HistoryEntry (UserId, CreatedAtUtc DESC, Id DESC);

CREATE TABLE IF NOT EXISTS dbo.ImageView
(
    UserId         INTEGER NOT NULL,
    GatewayImageId TEXT NOT NULL,
    ViewedAtUtc    TEXT NOT NULL,
    CONSTRAINT PK_ImageView PRIMARY KEY (UserId, GatewayImageId),
    CONSTRAINT FK_ImageView_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS dbo.HistoryMark
(
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    HistoryEntryId INTEGER NOT NULL,
    Token          TEXT NOT NULL,   -- plaintext token or its (longer) deterministic ciphertext
    Kind           INTEGER NOT NULL,
    CONSTRAINT FK_HistoryMark_Entry FOREIGN KEY (HistoryEntryId) REFERENCES HistoryEntry(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS dbo.IX_HistoryMark_Entry ON HistoryMark (HistoryEntryId);
CREATE INDEX IF NOT EXISTS dbo.IX_HistoryMark_Token ON HistoryMark (Token, Kind);

CREATE TABLE IF NOT EXISTS dbo.TokenBookmark
(
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId      INTEGER NOT NULL,
    Name        TEXT NOT NULL,   -- plaintext name or its (longer) deterministic ciphertext
    Kind        INTEGER NOT NULL,
    SavedAtUtc  TEXT NOT NULL,
    PinnedAtUtc TEXT NULL,
    CONSTRAINT FK_TokenBookmark_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_TokenBookmark_User_Name_Kind UNIQUE (UserId, Name, Kind)
);

CREATE TABLE IF NOT EXISTS dbo.ImageBookmark
(
    Id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId               INTEGER NOT NULL,
    GatewayImageId       TEXT NOT NULL,
    Prompt               TEXT NOT NULL,
    ModelFriendly        TEXT NOT NULL,
    ModelId              TEXT NOT NULL,
    Aspect               TEXT NOT NULL,
    OriginalCreatedAtUtc TEXT NOT NULL,
    SavedAtUtc           TEXT NOT NULL,
    CONSTRAINT FK_ImageBookmark_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_ImageBookmark_User_Image UNIQUE (UserId, GatewayImageId)
);
CREATE INDEX IF NOT EXISTS dbo.IX_ImageBookmark_User_Saved
    ON ImageBookmark (UserId, SavedAtUtc DESC, Id DESC);

CREATE TABLE IF NOT EXISTS dbo.ImageBookmarkMark
(
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ImageBookmarkId INTEGER NOT NULL,
    Token           TEXT NOT NULL,   -- plaintext token or its (longer) deterministic ciphertext
    Kind            INTEGER NOT NULL,
    CONSTRAINT FK_ImageBookmarkMark_Bookmark FOREIGN KEY (ImageBookmarkId) REFERENCES ImageBookmark(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS dbo.IX_ImageBookmarkMark_Bookmark ON ImageBookmarkMark (ImageBookmarkId);

CREATE TABLE IF NOT EXISTS dbo.TokenBookmarkCategory
(
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    TokenBookmarkId INTEGER NOT NULL,
    Category        TEXT NOT NULL,   -- plaintext category name or its (longer) deterministic ciphertext
    CONSTRAINT FK_TokenBookmarkCategory_Bookmark FOREIGN KEY (TokenBookmarkId) REFERENCES TokenBookmark(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_TokenBookmarkCategory UNIQUE (TokenBookmarkId, Category)
);
CREATE INDEX IF NOT EXISTS dbo.IX_TokenBookmarkCategory_Bookmark ON TokenBookmarkCategory (TokenBookmarkId);

CREATE TABLE IF NOT EXISTS dbo.ImageBookmarkCategory
(
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ImageBookmarkId INTEGER NOT NULL,
    Category        TEXT NOT NULL,   -- plaintext category name or its (longer) deterministic ciphertext
    CONSTRAINT FK_ImageBookmarkCategory_Bookmark FOREIGN KEY (ImageBookmarkId) REFERENCES ImageBookmark(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_ImageBookmarkCategory UNIQUE (ImageBookmarkId, Category)
);
CREATE INDEX IF NOT EXISTS dbo.IX_ImageBookmarkCategory_Bookmark ON ImageBookmarkCategory (ImageBookmarkId);

CREATE TABLE IF NOT EXISTS dbo.BannedToken
(
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId     INTEGER NOT NULL,
    ModelId    TEXT NOT NULL,
    Name       TEXT NOT NULL,   -- plaintext name or its (longer) deterministic ciphertext
    Kind       INTEGER NOT NULL,
    SavedAtUtc TEXT NOT NULL,
    CONSTRAINT FK_BannedToken_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_BannedToken_User_Model_Name_Kind UNIQUE (UserId, ModelId, Name, Kind)
);
CREATE INDEX IF NOT EXISTS dbo.IX_BannedToken_User_Model ON BannedToken (UserId, ModelId);

CREATE TABLE IF NOT EXISTS dbo.PendingJob
(
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId        INTEGER NOT NULL,
    JobId         TEXT NOT NULL,
    Prompt        TEXT NOT NULL,
    ModelFriendly TEXT NOT NULL,
    ModelId       TEXT NOT NULL,
    Aspect        TEXT NOT NULL,
    CreatedAtUtc  TEXT NOT NULL,
    CONSTRAINT FK_PendingJob_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_PendingJob_User_Job UNIQUE (UserId, JobId)
);
CREATE INDEX IF NOT EXISTS dbo.IX_PendingJob_Created ON PendingJob (CreatedAtUtc ASC, Id ASC);

CREATE TABLE IF NOT EXISTS dbo.ArtistDisplay
(
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId         INTEGER NOT NULL,
    ArtistName     TEXT NOT NULL,   -- plaintext artist token or its (longer) deterministic ciphertext
    GatewayImageId TEXT NOT NULL,
    SetAtUtc       TEXT NOT NULL,
    CONSTRAINT FK_ArtistDisplay_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_ArtistDisplay_User_Artist UNIQUE (UserId, ArtistName)
);

CREATE TABLE IF NOT EXISTS dbo.ImageBlob
(
    ImageId         TEXT PRIMARY KEY,
    Bytes           BLOB NOT NULL,
    ContentType     TEXT NOT NULL DEFAULT 'image/png',
    Width           INTEGER NULL,
    Height          INTEGER NULL,
    ByteSize        INTEGER NOT NULL,
    Kind            INTEGER NOT NULL DEFAULT 0,   -- 0=generated (1=upload: historical, no longer written)
    CreatedAtUtc    TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PaletteJson     TEXT NULL,
    FrequenciesJson TEXT NULL
);

CREATE TABLE IF NOT EXISTS dbo.ImageFrame
(
    ImageId    TEXT NOT NULL,
    FrameIndex INTEGER NOT NULL,
    Bytes      BLOB NOT NULL,
    CONSTRAINT PK_ImageFrame PRIMARY KEY (ImageId, FrameIndex)
);

CREATE TABLE IF NOT EXISTS dbo.GenTiming
(
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineName  TEXT NOT NULL,
    ConfigId     TEXT NOT NULL,   -- the workflow configuration id (the 'model' submitted)
    IsEdit       INTEGER NOT NULL,
    DurationMs   INTEGER NOT NULL,   -- ComfyUI submit -> image ready (queue wait excluded)
    CreatedAtUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS dbo.IX_GenTiming_Machine_Config
    ON GenTiming (MachineName ASC, ConfigId ASC, Id DESC);

CREATE TABLE IF NOT EXISTS dbo.Job
(
    JobId         TEXT PRIMARY KEY,   -- our GUID; the public job handle
    UserId        INTEGER NOT NULL,
    MachineName   TEXT NOT NULL,      -- owning instance (only it reconciles)
    Model         TEXT NOT NULL,      -- display: the job's configuration id
    Prompt        TEXT NOT NULL,      -- display: the job's prompt/instruction
    Total         INTEGER NOT NULL,   -- slot count (images this job will make)
    Status        INTEGER NOT NULL,   -- 0=Active, 1=Done, 2=Error, 3=Cancelled (job-level)
    CreatedAtUtc  TEXT NOT NULL,
    FinishedAtUtc TEXT NULL,          -- set when all slots terminal (finalized)
    CONSTRAINT FK_Job_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS dbo.IX_Job_Machine_Status ON Job (MachineName, Status, CreatedAtUtc ASC);
CREATE INDEX IF NOT EXISTS dbo.IX_Job_User_Status_Created ON Job (UserId, Status, CreatedAtUtc ASC, JobId ASC);

CREATE TABLE IF NOT EXISTS dbo.JobSlot
(
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    JobId              TEXT NOT NULL,
    SlotIndex          INTEGER NOT NULL,   -- position in the job's image array
    IsEdit             INTEGER NOT NULL,
    State              INTEGER NOT NULL,   -- 0=Queued, 1=Running, 2=Done, 3=Error, 4=Cancelled
    ComfyPromptId      TEXT NULL,          -- ComfyUI prompt id (internal; liveness key)
    ImageId            TEXT NULL,          -- produced image (dbo.ImageBlob id)
    Width              INTEGER NULL,
    Height             INTEGER NULL,
    Changed            INTEGER NOT NULL DEFAULT 1,   -- edits: false = model declined
    ChangeScore        REAL NULL,          -- edits only (pHash distance)
    Error              TEXT NULL,
    EffectivePrompt    TEXT NULL,          -- prompt actually rendered (markers/random handled)
    RawPrompt          TEXT NULL,          -- prompt VERBATIM in marker form, random injections included
    RawNegativePrompt  TEXT NULL,          -- negative VERBATIM in marker form
    MarksJson          TEXT NULL,          -- SUPERSEDED by dbo.JobSlotMark (kept: parity with schema.sql)
    RequestJson        TEXT NULL,          -- SUPERSEDED by the typed spec columns below
    GenStartedAtUtc    TEXT NULL,          -- when the render started (excludes queue wait)
    ExpectedGenSeconds REAL NULL,          -- ETA seed (machine+model recent average)
    -- The typed spec, added on SQL Server after RequestJson: the user's text encrypted, the rest queryable.
    Workflow           TEXT NULL,
    Prompt             TEXT NULL,          -- generate prompt / edit instruction (ENCRYPTED)
    NegativePrompt     TEXT NULL,          -- (ENCRYPTED)
    Aspect             TEXT NULL,
    RandomArtist       INTEGER NULL,
    RandomPrompt       INTEGER NULL,
    Temperature        REAL NULL,
    TagTypesJson       TEXT NULL,
    OverridesJson      TEXT NULL,
    SourceImageId      TEXT NULL,
    MaskImageId        TEXT NULL,
    LastFrameImageId   TEXT NULL,
    CONSTRAINT FK_JobSlot_Job FOREIGN KEY (JobId) REFERENCES Job(JobId) ON DELETE CASCADE,
    -- Load-bearing twice over: it is the conflict target of the slot upsert, AND the unique index the two composite
    -- foreign keys below resolve against. SQLite requires a UNIQUE index on the parent columns for those to be legal.
    CONSTRAINT UQ_JobSlot_Job_Index UNIQUE (JobId, SlotIndex)
);
CREATE INDEX IF NOT EXISTS dbo.IX_JobSlot_Job ON JobSlot (JobId, SlotIndex ASC);

CREATE TABLE IF NOT EXISTS dbo.JobSlotReference
(
    JobId     TEXT NOT NULL,
    SlotIndex INTEGER NOT NULL,
    Ordinal   INTEGER NOT NULL,
    ImageId   TEXT NOT NULL,
    CONSTRAINT PK_JobSlotReference PRIMARY KEY (JobId, SlotIndex, Ordinal),
    CONSTRAINT FK_JobSlotReference_Slot FOREIGN KEY (JobId, SlotIndex)
        REFERENCES JobSlot(JobId, SlotIndex) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS dbo.IX_JobSlotReference_Image ON JobSlotReference (ImageId);

CREATE TABLE IF NOT EXISTS dbo.JobSlotMark
(
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    JobId     TEXT NOT NULL,
    SlotIndex INTEGER NOT NULL,
    Token     TEXT NOT NULL,   -- deterministic ciphertext of the canonical token
    Kind      INTEGER NOT NULL,
    CONSTRAINT FK_JobSlotMark_Slot FOREIGN KEY (JobId, SlotIndex)
        REFERENCES JobSlot(JobId, SlotIndex) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS dbo.IX_JobSlotMark_Slot ON JobSlotMark (JobId, SlotIndex);
CREATE INDEX IF NOT EXISTS dbo.IX_JobSlotMark_Token ON JobSlotMark (Token, Kind);

CREATE TABLE IF NOT EXISTS dbo.UserFavoriteWorkflow
(
    UserId     INTEGER NOT NULL,
    WorkflowId TEXT NOT NULL,
    CONSTRAINT PK_UserFavoriteWorkflow PRIMARY KEY (UserId, WorkflowId),
    CONSTRAINT FK_UserFavoriteWorkflow_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS dbo.UserHiddenWorkflow
(
    UserId     INTEGER NOT NULL,
    WorkflowId TEXT NOT NULL,
    CONSTRAINT PK_UserHiddenWorkflow PRIMARY KEY (UserId, WorkflowId),
    CONSTRAINT FK_UserHiddenWorkflow_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE
);

-- Hidden from the API workflow list, independent of the UI picker: a user can keep a workflow in their
-- picker but out of what their API key returns, or vice versa. Mirrors dbo.UserHiddenWorkflow.
CREATE TABLE IF NOT EXISTS dbo.UserHiddenApiWorkflow
(
    UserId     INTEGER NOT NULL,
    WorkflowId TEXT NOT NULL,
    CONSTRAINT PK_UserHiddenApiWorkflow PRIMARY KEY (UserId, WorkflowId),
    CONSTRAINT FK_UserHiddenApiWorkflow_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS dbo.UserWorkflowTag
(
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId     INTEGER NOT NULL,
    WorkflowId TEXT NOT NULL,
    Tag        TEXT NOT NULL,   -- deterministic ciphertext of the user's own label
    CONSTRAINT FK_UserWorkflowTag_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS dbo.IX_UserWorkflowTag_User ON UserWorkflowTag (UserId, WorkflowId);

CREATE TABLE IF NOT EXISTS dbo.UserLog
(
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId       INTEGER NOT NULL,
    Category     TEXT NOT NULL,
    Payload      TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    CONSTRAINT FK_UserLog_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS dbo.IX_UserLog_User_Created ON UserLog (UserId, CreatedAtUtc DESC, Id DESC);

-- Install-wide catalogue overrides -- see schema.sql for why these are per-MACHINE and not per-user.
-- Note the SQLite asymmetry: the TABLE name is dbo-qualified, the INDEX name is qualified but the table it
-- names is NOT, and a FOREIGN KEY may never be qualified. There are no FKs here (a slot id is a catalogue
-- identity, not a row).
CREATE TABLE IF NOT EXISTS dbo.ModelBinding
(
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineName  TEXT NOT NULL,
    SlotId       TEXT NOT NULL,
    FileName     TEXT NOT NULL,
    IsAuto       INTEGER NOT NULL DEFAULT 0,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS dbo.UX_ModelBinding_Machine_Slot ON ModelBinding (MachineName, SlotId);

CREATE TABLE IF NOT EXISTS dbo.ConfigOverride
(
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineName  TEXT NOT NULL,
    ConfigId     TEXT NOT NULL,
    SettingKey   TEXT NOT NULL,
    SettingValue TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS dbo.UX_ConfigOverride_Machine_Config_Key ON ConfigOverride (MachineName, ConfigId, SettingKey);

-- This machine's own configuration -- the keys that used to live in appsettings.json. Per-MACHINE for the same
-- reason ModelBinding is: one database can back several app instances, and the renderer's address is a property
-- of the box, not of the install. A key lives HERE or in the file, never both; the file keeps only what is needed
-- to open this database (see MachineSettingsConfigurationSource).
CREATE TABLE IF NOT EXISTS dbo.MachineSetting
(
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineName  TEXT NOT NULL,
    SettingKey   TEXT NOT NULL,
    SettingValue TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS dbo.UX_MachineSetting_Machine_Key ON MachineSetting (MachineName, SettingKey);


-- --- 0.9.1 ------------------------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS dbo.HistoryLora
(
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    HistoryEntryId INTEGER NOT NULL,
    Name           TEXT NOT NULL,   -- the subfolder-qualified lora_name's deterministic ciphertext
    Weight         REAL NOT NULL,
    CONSTRAINT FK_HistoryLora_Entry FOREIGN KEY (HistoryEntryId) REFERENCES HistoryEntry(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS dbo.IX_HistoryLora_Entry ON HistoryLora (HistoryEntryId);
CREATE INDEX IF NOT EXISTS dbo.IX_HistoryLora_Name ON HistoryLora (Name);

CREATE TABLE IF NOT EXISTS dbo.LoraDisplay
(
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId         INTEGER NOT NULL,
    LoraName       TEXT NOT NULL,   -- subfolder-qualified lora_name's deterministic ciphertext
    GatewayImageId TEXT NOT NULL,
    SetAtUtc       TEXT NOT NULL,
    CONSTRAINT FK_LoraDisplay_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_LoraDisplay_User_Lora UNIQUE (UserId, LoraName)
);

-- Machine-level cache of what CivitAI knows about a LoRA file (looked up by hash). Not per-user; LoraName is the
-- plain subfolder-qualified filename (a shared machine asset, like dbo.ModelBinding.FileName).
CREATE TABLE IF NOT EXISTS dbo.LoraMeta
(
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    LoraName     TEXT NOT NULL,
    Sha256       TEXT NULL,
    TrainedWords TEXT NULL,          -- JSON array of CivitAI trigger words (may be [])
    ModelName    TEXT NULL,
    PreviewUrl   TEXT NULL,
    FetchedAtUtc TEXT NOT NULL,
    CONSTRAINT UQ_LoraMeta_Name UNIQUE (LoraName)
);

-- Per-user LoRA preferences: a trigger-word override (NULL = use the CivitAI default) and whether to auto-attach
-- the trigger words to the prompt. LoraName is deterministically encrypted, like dbo.LoraDisplay.
CREATE TABLE IF NOT EXISTS dbo.LoraUserSetting
(
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId       INTEGER NOT NULL,
    LoraName     TEXT NOT NULL,
    TriggerWords TEXT NULL,
    AutoAttach   INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT FK_LoraUserSetting_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_LoraUserSetting_User_Lora UNIQUE (UserId, LoraName)
);

-- New column on the pre-existing JobSlot table. It MUST be an ALTER, never an edit to the 0.9.0 CREATE above: an
-- existing database skips that CREATE (the table is already there), so an inlined column would never arrive. The
-- runner adds it only when absent (PRAGMA table_info), so replaying this is a no-op. user LoRA stack for this
-- slot: [{name,weight}] (plain, per-slot durable).
ALTER TABLE dbo.JobSlot ADD COLUMN LorasJson TEXT NULL;


-- --- 0.9.2 ------------------------------------------------------------------------------------------------------

-- A user's chosen portrait image for a tag (the bookmarks page). Mirrors dbo.ArtistDisplay/LoraDisplay.
CREATE TABLE IF NOT EXISTS dbo.TagDisplay
(
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId         INTEGER NOT NULL,
    TagName        TEXT NOT NULL,   -- canonical tag token's deterministic ciphertext
    GatewayImageId TEXT NOT NULL,
    SetAtUtc       TEXT NOT NULL,
    CONSTRAINT FK_TagDisplay_User FOREIGN KEY (UserId) REFERENCES AppUser(Id) ON DELETE CASCADE,
    CONSTRAINT UQ_TagDisplay_User_Tag UNIQUE (UserId, TagName)
);


-- --- 0.9.3 ------------------------------------------------------------------------------------------------------

-- Machine-level cache of a LoRA's CivitAI preview media (an image, or a short clip -- some previews are mp4).
-- Downloaded once and served from this box rather than hotlinking the CivitAI CDN. Keyed by the plain filename,
-- like dbo.LoraMeta.
CREATE TABLE IF NOT EXISTS dbo.LoraPreview
(
    LoraName     TEXT PRIMARY KEY,
    Bytes        BLOB NOT NULL,
    ContentType  TEXT NOT NULL,
    FetchedAtUtc TEXT NOT NULL
);
