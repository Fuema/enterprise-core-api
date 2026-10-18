/*
  003 - Role-based access control, account security, sessions and audit log.
  Idempotent: every DDL statement checks first; the one-time data migration is recorded in
  dbo.SchemaMigrations ('003_rbac_data') and never runs twice.

  After this script:
    effective permissions of a user = permissions of all their roles  UNION  their direct grants
  The legacy comma-separated columns AppUsers.Roles / AppUsers.Permissions are migrated and then
  no longer read by the API (kept for rollback; drop them in a later script).
*/

IF OBJECT_ID(N'dbo.SchemaMigrations', N'U') IS NULL
    CREATE TABLE dbo.SchemaMigrations (
        Id            nvarchar(100) NOT NULL CONSTRAINT PK_SchemaMigrations PRIMARY KEY,
        AppliedAtUtc  datetime2     NOT NULL CONSTRAINT DF_SchemaMigrations_AppliedAtUtc DEFAULT SYSUTCDATETIME()
    );
GO

-- ---------------------------------------------------------------- roles
IF OBJECT_ID(N'dbo.Roles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Roles (
        Id            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
        Name          nvarchar(64)  NOT NULL,
        Description   nvarchar(200) NULL,
        IsSystem      bit           NOT NULL CONSTRAINT DF_Roles_IsSystem DEFAULT 0,
        CreatedAtUtc  datetime2     NOT NULL CONSTRAINT DF_Roles_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc  datetime2     NULL
    );
    CREATE UNIQUE INDEX UX_Roles_Name ON dbo.Roles (Name);
END
GO

IF OBJECT_ID(N'dbo.RolePermissions', N'U') IS NULL
    CREATE TABLE dbo.RolePermissions (
        RoleId      int           NOT NULL CONSTRAINT FK_RolePermissions_Roles REFERENCES dbo.Roles (Id) ON DELETE CASCADE,
        Permission  nvarchar(100) NOT NULL,
        CONSTRAINT PK_RolePermissions PRIMARY KEY (RoleId, Permission)
    );
GO

IF OBJECT_ID(N'dbo.UserRoles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserRoles (
        UserId  int NOT NULL CONSTRAINT FK_UserRoles_AppUsers REFERENCES dbo.AppUsers (Id) ON DELETE CASCADE,
        RoleId  int NOT NULL CONSTRAINT FK_UserRoles_Roles REFERENCES dbo.Roles (Id) ON DELETE CASCADE,
        CONSTRAINT PK_UserRoles PRIMARY KEY (UserId, RoleId)
    );
    CREATE INDEX IX_UserRoles_RoleId ON dbo.UserRoles (RoleId);
END
GO

-- Direct grants: extra permissions on top of the user's roles.
IF OBJECT_ID(N'dbo.UserPermissions', N'U') IS NULL
    CREATE TABLE dbo.UserPermissions (
        UserId      int           NOT NULL CONSTRAINT FK_UserPermissions_AppUsers REFERENCES dbo.AppUsers (Id) ON DELETE CASCADE,
        Permission  nvarchar(100) NOT NULL,
        CONSTRAINT PK_UserPermissions PRIMARY KEY (UserId, Permission)
    );
GO

-- ---------------------------------------------------------------- audit log (no FKs: history outlives accounts)
IF OBJECT_ID(N'dbo.AuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLog (
        Id             bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
        OccurredAtUtc  datetime2     NOT NULL,
        ActorUserId    int           NULL,
        ActorName      nvarchar(100) NULL,
        Action         nvarchar(64)  NOT NULL,
        TargetType     nvarchar(32)  NOT NULL,
        TargetId       nvarchar(64)  NULL,
        TargetName     nvarchar(100) NULL,
        Details        nvarchar(max) NULL,
        IpAddress      nvarchar(64)  NULL
    );
    CREATE INDEX IX_AuditLog_OccurredAtUtc ON dbo.AuditLog (OccurredAtUtc DESC);
    CREATE INDEX IX_AuditLog_Target ON dbo.AuditLog (TargetType, TargetId);
END
GO

-- ---------------------------------------------------------------- account security columns
IF COL_LENGTH(N'dbo.AppUsers', N'SecurityStamp') IS NULL
    ALTER TABLE dbo.AppUsers ADD SecurityStamp nvarchar(64) NOT NULL
        CONSTRAINT DF_AppUsers_SecurityStamp DEFAULT (REPLACE(CONVERT(nvarchar(64), NEWID()), N'-', N''));
GO
IF COL_LENGTH(N'dbo.AppUsers', N'FailedLoginCount') IS NULL
    ALTER TABLE dbo.AppUsers ADD FailedLoginCount int NOT NULL CONSTRAINT DF_AppUsers_FailedLoginCount DEFAULT 0;
GO
IF COL_LENGTH(N'dbo.AppUsers', N'LockoutEndUtc') IS NULL
    ALTER TABLE dbo.AppUsers ADD LockoutEndUtc datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.AppUsers', N'MustChangePassword') IS NULL
    ALTER TABLE dbo.AppUsers ADD MustChangePassword bit NOT NULL CONSTRAINT DF_AppUsers_MustChangePassword DEFAULT 0;
GO
IF COL_LENGTH(N'dbo.AppUsers', N'LastLoginAtUtc') IS NULL
    ALTER TABLE dbo.AppUsers ADD LastLoginAtUtc datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.AppUsers', N'PasswordChangedAtUtc') IS NULL
    ALTER TABLE dbo.AppUsers ADD PasswordChangedAtUtc datetime2 NULL;
GO

-- ---------------------------------------------------------------- sessions (a session = chain of rotated refresh tokens)
IF COL_LENGTH(N'dbo.RefreshTokens', N'SessionId') IS NULL
    ALTER TABLE dbo.RefreshTokens ADD SessionId uniqueidentifier NULL;
GO
IF COL_LENGTH(N'dbo.RefreshTokens', N'SessionStartedAtUtc') IS NULL
    ALTER TABLE dbo.RefreshTokens ADD SessionStartedAtUtc datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.RefreshTokens', N'IpAddress') IS NULL
    ALTER TABLE dbo.RefreshTokens ADD IpAddress nvarchar(64) NULL;
GO
IF COL_LENGTH(N'dbo.RefreshTokens', N'UserAgent') IS NULL
    ALTER TABLE dbo.RefreshTokens ADD UserAgent nvarchar(256) NULL;
GO
-- Tokens issued before this script: each one becomes its own session.
UPDATE dbo.RefreshTokens
SET SessionId = NEWID(), SessionStartedAtUtc = CreatedAtUtc
WHERE SessionId IS NULL OR SessionStartedAtUtc IS NULL;
GO
IF COLUMNPROPERTY(OBJECT_ID(N'dbo.RefreshTokens'), N'SessionId', 'AllowsNull') = 1
    ALTER TABLE dbo.RefreshTokens ALTER COLUMN SessionId uniqueidentifier NOT NULL;
GO
IF COLUMNPROPERTY(OBJECT_ID(N'dbo.RefreshTokens'), N'SessionStartedAtUtc', 'AllowsNull') = 1
    ALTER TABLE dbo.RefreshTokens ALTER COLUMN SessionStartedAtUtc datetime2 NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RefreshTokens_SessionId' AND object_id = OBJECT_ID(N'dbo.RefreshTokens'))
    CREATE INDEX IX_RefreshTokens_SessionId ON dbo.RefreshTokens (SessionId);
GO

-- ---------------------------------------------------------------- one-time data migration
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaMigrations WHERE Id = N'003_rbac_data')
BEGIN
    SET NOCOUNT ON;

    -- Legacy comma-separated values as rows.
    SELECT DISTINCT u.Id AS UserId, LTRIM(RTRIM(r.value)) AS RoleName
    INTO #UserRoleNames
    FROM dbo.AppUsers u
    CROSS APPLY STRING_SPLIT(u.Roles, N',') r
    WHERE LTRIM(RTRIM(r.value)) <> N'';

    SELECT DISTINCT u.Id AS UserId, LTRIM(RTRIM(p.value)) AS Permission
    INTO #UserPerms
    FROM dbo.AppUsers u
    CROSS APPLY STRING_SPLIT(u.Permissions, N',') p
    WHERE LTRIM(RTRIM(p.value)) <> N'';

    -- 1. Roles from the names in use; Administrator is always present and locked.
    INSERT dbo.Roles (Name, IsSystem)
    SELECT DISTINCT n.RoleName, 0
    FROM #UserRoleNames n
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles r WHERE r.Name = n.RoleName);

    IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE Name = N'Administrator')
        INSERT dbo.Roles (Name, IsSystem) VALUES (N'Administrator', 1);

    UPDATE dbo.Roles
    SET IsSystem = 1, Description = COALESCE(Description, N'Full access to every feature, including future ones')
    WHERE Name = N'Administrator';

    -- 2. Memberships.
    INSERT dbo.UserRoles (UserId, RoleId)
    SELECT DISTINCT n.UserId, r.Id
    FROM #UserRoleNames n
    JOIN dbo.Roles r ON r.Name = n.RoleName
    WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles x WHERE x.UserId = n.UserId AND x.RoleId = r.Id);

    -- 3. A role gets the permissions that EVERY one of its members had.
    INSERT dbo.RolePermissions (RoleId, Permission)
    SELECT ur.RoleId, up.Permission
    FROM dbo.UserRoles ur
    JOIN #UserPerms up ON up.UserId = ur.UserId
    JOIN dbo.Roles r ON r.Id = ur.RoleId AND r.IsSystem = 0
    GROUP BY ur.RoleId, up.Permission
    HAVING COUNT(DISTINCT ur.UserId) = (SELECT COUNT(*) FROM dbo.UserRoles m WHERE m.RoleId = ur.RoleId);

    -- Administrator = exactly '*'.
    DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId WHERE r.Name = N'Administrator';
    INSERT dbo.RolePermissions (RoleId, Permission) SELECT Id, N'*' FROM dbo.Roles WHERE Name = N'Administrator';

    -- 4. Whatever a user's roles do not already grant becomes a direct grant: effective access is unchanged.
    INSERT dbo.UserPermissions (UserId, Permission)
    SELECT up.UserId, up.Permission
    FROM #UserPerms up
    WHERE NOT EXISTS (
        SELECT 1
        FROM dbo.UserRoles ur
        JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
        WHERE ur.UserId = up.UserId AND (rp.Permission = up.Permission OR rp.Permission = N'*'));

    INSERT dbo.SchemaMigrations (Id) VALUES (N'003_rbac_data');
    PRINT '003: migrated legacy roles/permissions into Roles, UserRoles, RolePermissions and UserPermissions.';
END
GO
