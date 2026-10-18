using System.Text.Json;
using AngularCrudApi.Data;
using AngularCrudApi.Models;

namespace AngularCrudApi.Auth;

/// <summary>Audit action names (stable strings: the UI filters on them).</summary>
public static class AuditActions
{
    public const string LoginSucceeded = "auth.login";
    public const string LoginFailed = "auth.login_failed";
    public const string LockedOut = "auth.locked_out";
    public const string Signup = "auth.signup";
    public const string PasswordChanged = "auth.password_changed";
    public const string SessionRevoked = "auth.session_revoked";

    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserPasswordReset = "user.password_reset";
    public const string UserUnlocked = "user.unlocked";
    public const string UserSessionsRevoked = "user.sessions_revoked";

    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleDeleted = "role.deleted";

    public static readonly string[] All =
    [
        LoginSucceeded, LoginFailed, LockedOut, Signup, PasswordChanged, SessionRevoked,
        UserCreated, UserUpdated, UserPasswordReset, UserUnlocked, UserSessionsRevoked,
        RoleCreated, RoleUpdated, RoleDeleted,
    ];
}

/// <summary>
/// Appends audit entries to the CURRENT DbContext: they are saved by the caller's SaveChangesAsync, in the
/// same transaction as the change they describe (no change without its audit record, and vice versa).
/// The actor is the signed-in user unless given explicitly (e.g. a login, where nobody is signed in yet).
/// </summary>
public sealed class AuditService(AppDbContext db, IHttpContextAccessor httpContextAccessor)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Record(
        string action, string targetType, string? targetId, string? targetName,
        object? details = null, int? actorUserId = null, string? actorName = null)
    {
        var http = httpContextAccessor.HttpContext;
        var user = http?.User;
        if (actorUserId is null && int.TryParse(user?.FindFirst(AppClaims.Subject)?.Value, out var signedIn))
        {
            actorUserId = signedIn;
            actorName ??= user?.FindFirst(AppClaims.Name)?.Value;
        }

        db.AuditLog.Add(new AuditEntry
        {
            OccurredAtUtc = DateTime.UtcNow,
            ActorUserId = actorUserId,
            ActorName = Truncate(actorName, 100),
            Action = action,
            TargetType = targetType,
            TargetId = Truncate(targetId, 64),
            TargetName = Truncate(targetName, 100),
            Details = details is null ? null : JsonSerializer.Serialize(details, Json),
            IpAddress = Truncate(http?.Connection.RemoteIpAddress?.ToString(), 64),
        });
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
