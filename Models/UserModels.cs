using System.ComponentModel.DataAnnotations;

namespace AngularCrudApi.Models;

/// <summary>Account as shown on the admin screen. Never contains the password hash.</summary>
public sealed record UserResponse(
    int Id,
    string Username,
    string DisplayName,
    string? Email,
    string[] Roles,
    string[] Permissions,
    bool IsActive,
    DateTime CreatedAtUtc)
{
    public static UserResponse From(AppUser user) =>
        new(user.Id, user.Username, user.DisplayName, user.Email, user.GetRoles(), user.GetPermissions(), user.IsActive, user.CreatedAtUtc);
}

/// <summary>Body of PUT /api/users/{id}. The username and password are not editable here.</summary>
public class UserUpdateRequest
{
    [Required(ErrorMessage = "Full name is required"), StringLength(100, ErrorMessage = "Max 100 characters")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional; null or empty clears it.</summary>
    [EmailAddress(ErrorMessage = "Enter a valid email address"), StringLength(256, ErrorMessage = "Max 256 characters")]
    public string? Email { get; set; }

    [MaxLength(10, ErrorMessage = "At most 10 roles")]
    public List<string> Roles { get; set; } = new();

    /// <summary>Scopes from GET /api/users/permissions, "resource:*" or "*".</summary>
    [MaxLength(50, ErrorMessage = "At most 50 permissions")]
    public List<string> Permissions { get; set; } = new();

    public bool IsActive { get; set; } = true;
}

/// <summary>Every grantable scope, so the UI can build its permission editor from the API.</summary>
public sealed record PermissionCatalogResponse(string[] Scopes);
