/*
  001 - Authentication tables + unique employee email.
  Idempotent: safe to run any number of times (SSMS, sqlcmd, or automatically at startup
  when Database:ApplyBootstrapScripts=true). Batches are separated by GO.
*/

IF OBJECT_ID(N'dbo.AppUsers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AppUsers (
        Id            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AppUsers PRIMARY KEY,
        Username      nvarchar(64)   NOT NULL,
        DisplayName   nvarchar(100)  NOT NULL,
        Email         nvarchar(256)  NULL,
        PasswordHash  nvarchar(512)  NOT NULL,
        Roles         nvarchar(400)  NOT NULL CONSTRAINT DF_AppUsers_Roles DEFAULT N'',
        Permissions   nvarchar(1000) NOT NULL CONSTRAINT DF_AppUsers_Permissions DEFAULT N'',
        IsActive      bit            NOT NULL CONSTRAINT DF_AppUsers_IsActive DEFAULT 1,
        CreatedAtUtc  datetime2      NOT NULL CONSTRAINT DF_AppUsers_CreatedAtUtc DEFAULT SYSUTCDATETIME()
    );
    CREATE UNIQUE INDEX UX_AppUsers_Username ON dbo.AppUsers (Username);
END
GO

IF OBJECT_ID(N'dbo.RefreshTokens', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RefreshTokens (
        Id            bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RefreshTokens PRIMARY KEY,
        UserId        int        NOT NULL CONSTRAINT FK_RefreshTokens_AppUsers REFERENCES dbo.AppUsers (Id) ON DELETE CASCADE,
        TokenHash     char(64)   NOT NULL,
        CreatedAtUtc  datetime2  NOT NULL,
        ExpiresAtUtc  datetime2  NOT NULL,
        RevokedAtUtc  datetime2  NULL
    );
    CREATE UNIQUE INDEX UX_RefreshTokens_TokenHash ON dbo.RefreshTokens (TokenHash);
    CREATE INDEX IX_RefreshTokens_UserId ON dbo.RefreshTokens (UserId);
END
GO

-- One employee per email address. Skipped (with a message) when the column cannot be indexed
-- or duplicates already exist; the API also checks uniqueness before saving.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_tblEmployeeMaster_EmailId' AND object_id = OBJECT_ID(N'dbo.tblEmployeeMaster'))
BEGIN
    IF COL_LENGTH(N'dbo.tblEmployeeMaster', N'EmailId') = -1
        PRINT 'Skipped UX_tblEmployeeMaster_EmailId: EmailId is nvarchar(max). ALTER it to nvarchar(100) and re-run.';
    ELSE IF EXISTS (SELECT EmailId FROM dbo.tblEmployeeMaster GROUP BY EmailId HAVING COUNT(*) > 1)
        PRINT 'Skipped UX_tblEmployeeMaster_EmailId: duplicate EmailId values exist. Fix them and re-run.';
    ELSE
        EXEC (N'CREATE UNIQUE INDEX UX_tblEmployeeMaster_EmailId ON dbo.tblEmployeeMaster (EmailId);');
END
GO
