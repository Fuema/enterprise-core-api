using System.ComponentModel.DataAnnotations;

namespace AngularCrudApi.Models;

/// <summary>Row of GET /api/users.</summary>
public sealed class UserListItem
{
    public int Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public bool IsActive { get; init; }
    public DateTime? LockoutEndUtc { get; init; }
    public bool MustChangePassword { get; init; }
    public DateTime? LastLoginAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public List<RoleRef> Roles { get; init; } = [];
    public int DirectPermissionCount { get; init; }
}

public sealed record RoleRef(int Id, string Name, bool IsSystem);

/// <summary>GET /api/users/{id}: everything the admin screen shows. Never contains the password hash.</summary>
public sealed record UserDetailResponse(
    int Id,
    string Username,
    string DisplayName,
    string? Email,
    bool IsActive,
    DateTime? LockoutEndUtc,
    int FailedLoginCount,
    bool MustChangePassword,
    DateTime? LastLoginAtUtc,
    DateTime? PasswordChangedAtUtc,
    DateTime CreatedAtUtc,
    RoleRef[] Roles,
    string[] DirectPermissions,
    string[] EffectivePermissions);

/// <summary>Body of POST /api/users (an administrator creates an account with a temporary password).</summary>
public class UserCreateRequest
{
    [Required(ErrorMessage = "Username is required"),
     StringLength(64, MinimumLength = 3, ErrorMessage = "Use 3 to 64 characters"),
     RegularExpression(@"^[A-Za-z0-9._-]+$", ErrorMessage = "Use letters, digits, dots, dashes or underscores")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Full name is required"), StringLength(100, ErrorMessage = "Max 100 characters")]
    public string DisplayName { get; set; } = string.Empty;

    [EmailAddress(ErrorMessage = "Enter a valid email address"), StringLength(256, ErrorMessage = "Max 256 characters")]
    public string? Email { get; set; }

    [MaxLength(20, ErrorMessage = "At most 20 roles")]
    public List<int> RoleIds { get; set; } = new();

    [MaxLength(50, ErrorMessage = "At most 50 permissions")]
    public List<string> DirectPermissions { get; set; } = new();
}

/// <summary>Body of PUT /api/users/{id}. Username and password are not editable here.</summary>
public class UserUpdateRequest
{
    [Required(ErrorMessage = "Full name is required"), StringLength(100, ErrorMessage = "Max 100 characters")]
    public string DisplayName { get; set; } = string.Empty;

    [EmailAddress(ErrorMessage = "Enter a valid email address"), StringLength(256, ErrorMessage = "Max 256 characters")]
    public string? Email { get; set; }

    [MaxLength(20, ErrorMessage = "At most 20 roles")]
    public List<int> RoleIds { get; set; } = new();

    /// <summary>Extra permissions on top of the roles: scopes, "resource:*" or "*".</summary>
    [MaxLength(50, ErrorMessage = "At most 50 permissions")]
    public List<string> DirectPermissions { get; set; } = new();

    public bool IsActive { get; set; } = true;
}

/// <summary>POST /api/users: the new account and its one-time temporary password.</summary>
public sealed record UserCreatedResponse(UserDetailResponse User, string TemporaryPassword);

/// <summary>POST /api/users/{id}/reset-password: shown once to the administrator.</summary>
public sealed record TemporaryPasswordResponse(string TemporaryPassword);

/// <summary>Every grantable scope, so the UI can build its permission editor from the API.</summary>
public sealed record PermissionCatalogResponse(string[] Scopes);

/// <summary>GET /api/users/role-options: roles for the user form, with permissions to show what each adds.</summary>
public sealed record RoleOption(int Id, string Name, bool IsSystem, string[] Permissions);
