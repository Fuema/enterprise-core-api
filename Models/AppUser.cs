namespace AngularCrudApi.Models;

/// <summary>
/// Application account (table dbo.AppUsers). Passwords are ASP.NET Core Identity hashes.
/// Access = permissions of <see cref="UserRoles"/> + <see cref="DirectPermissions"/> (see AccessService).
/// The legacy columns AppUsers.Roles / AppUsers.Permissions are no longer mapped (script 003).
/// </summary>
public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Changes whenever access changes (roles, grants, deactivation, password). Access tokens carry it;
    /// a token with an old stamp is rejected on its next request (SessionValidator).
    /// </summary>
    public string SecurityStamp { get; set; } = NewSecurityStamp();

    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }

    /// <summary>Set by an administrator's password reset: every API call except change-password is refused.</summary>
    public bool MustChangePassword { get; set; }

    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime? PasswordChangedAtUtc { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<UserPermission> DirectPermissions { get; set; } = new List<UserPermission>();

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc is { } end && end > nowUtc;

    public void RotateSecurityStamp() => SecurityStamp = NewSecurityStamp();

    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");
}

/// <summary>Named set of permissions (table dbo.Roles). System roles cannot be renamed, emptied or deleted.</summary>
public class Role
{
    public const string AdministratorName = "Administrator";

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<RolePermission> Permissions { get; set; } = new List<RolePermission>();
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}

public class RolePermission
{
    public int RoleId { get; set; }
    public string Permission { get; set; } = string.Empty;
}

public class UserRole
{
    public int UserId { get; set; }
    public int RoleId { get; set; }
    public AppUser User { get; set; } = null!;
    public Role Role { get; set; } = null!;
}

/// <summary>Direct grant: a permission given to one user on top of their roles.</summary>
public class UserPermission
{
    public int UserId { get; set; }
    public string Permission { get; set; } = string.Empty;
}

/// <summary>
/// Rotating refresh token (table dbo.RefreshTokens). Only the SHA-256 hash is stored.
/// All tokens of one sign-in share a <see cref="SessionId"/>: that is what "sessions" lists and revokes.
/// </summary>
public class RefreshToken
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    public Guid SessionId { get; set; }
    public DateTime SessionStartedAtUtc { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}

/// <summary>Who did what to which account/role, and when (table dbo.AuditLog). Append-only.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public int? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public string? TargetName { get; set; }
    /// <summary>JSON (camelCase) describing the change.</summary>
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}
