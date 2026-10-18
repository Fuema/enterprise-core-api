using System.ComponentModel.DataAnnotations;

namespace AngularCrudApi.Models;

public class LoginRequest
{
    [Required(ErrorMessage = "Username is required")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>Principal returned by /api/auth/login and /api/auth/me (matches the React authUserSchema).</summary>
public sealed record AuthUserResponse(
    string Id, string DisplayName, string? Email, string[] Roles, string[] Permissions, bool MustChangePassword)
{
    public static AuthUserResponse From(AppUser user, Auth.UserAccess access) =>
        new(user.Id.ToString(), user.DisplayName, user.Email, access.Roles, access.Permissions, user.MustChangePassword);
}

public sealed record LoginResponse(string AccessToken, AuthUserResponse User);

public sealed record RefreshResponse(string AccessToken);

/// <summary>Body of POST /api/auth/register (self-service signup).</summary>
public class RegisterRequest
{
    [Required(ErrorMessage = "Username is required"),
     StringLength(64, MinimumLength = 3, ErrorMessage = "Use 3 to 64 characters"),
     RegularExpression(@"^[A-Za-z0-9._-]+$", ErrorMessage = "Use letters, digits, dots, dashes or underscores")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Full name is required"), StringLength(100, ErrorMessage = "Max 100 characters")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required"), EmailAddress(ErrorMessage = "Enter a valid email address"),
     StringLength(256, ErrorMessage = "Max 256 characters")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Complexity (upper, lower, digit) is checked in AuthEndpoints.Register.</summary>
    [Required(ErrorMessage = "Password is required"),
     StringLength(100, MinimumLength = 8, ErrorMessage = "Use at least 8 characters")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm your password"), Compare(nameof(Password), ErrorMessage = "Passwords do not match")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>Body of POST /api/auth/change-password.</summary>
public class ChangePasswordRequest
{
    [Required(ErrorMessage = "Current password is required")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "New password is required"),
     StringLength(100, MinimumLength = 8, ErrorMessage = "Use at least 8 characters")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm your new password"), Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>One signed-in device/browser (a chain of rotated refresh tokens).</summary>
public sealed record SessionResponse(
    string Id, DateTime StartedAtUtc, DateTime LastActiveAtUtc, DateTime ExpiresAtUtc,
    string? IpAddress, string? UserAgent, bool Current);
