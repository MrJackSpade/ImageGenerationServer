-- Change script: add the per-user editor-state blob (EditPrefs) to dbo.AppUser.
-- Run on the database box (204) with a DDL-capable login (the same one used for setup-database.ps1);
-- the app login (imagegen_app) intentionally has no DDL. Idempotent: safe to run more than once.
--
--   sqlcmd -S localhost -d ImageGen -E -i migrate_EditPrefs.sql        (Windows auth)
--   sqlcmd -S localhost -d ImageGen -U <ddl_login> -P <pw> -i migrate_EditPrefs.sql

USE ImageGen;
GO

-- STEP 1 — add EditPrefs (the edit-page analogue of ComposerPrefs; opaque JSON, encrypted at rest by the app).
-- SAFE TO RUN NOW, before deploying the new build: the currently-running build ignores the extra column.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'EditPrefs' AND Object_ID = OBJECT_ID('dbo.AppUser'))
    ALTER TABLE dbo.AppUser ADD EditPrefs NVARCHAR(MAX) NULL;
GO

-- STEP 2 — OPTIONAL cleanup of the superseded EditWorkflowId column.
-- RUN ONLY AFTER the new build is deployed everywhere. The new code no longer reads EditWorkflowId, but the
-- OLD build still SELECTs it, so dropping it before deploy would break the running app. Leaving the column in
-- place is harmless; this step is just tidiness. Idempotent.
--
-- IF EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'EditWorkflowId' AND Object_ID = OBJECT_ID('dbo.AppUser'))
--     ALTER TABLE dbo.AppUser DROP COLUMN EditWorkflowId;
-- GO
