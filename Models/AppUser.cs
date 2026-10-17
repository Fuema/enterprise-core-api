namespace AngularCrudApi.Models;

/// <summary>Application account (table dbo.AppUsers). Passwords are stored as ASP.NET Core Identity hashes.</summary>
public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Comma-separated role names, e.g. "Administrator".</summary>
    public string Roles { get; set; } = string.Empty;

    /// <summary>Comma-separated permission scopes, e.g. "employees:read,employees:write" or "*".</summary>
    public string Permissions { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string[] GetRoles() => Split(Roles);
    public string[] GetPermissions() => Split(Permissions);

    private static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Rotating refresh token (table dbo.RefreshTokens). Only the SHA-256 hash is stored.</summary>
public class RefreshToken
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}
