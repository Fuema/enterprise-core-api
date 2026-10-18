using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Infrastructure;
using AngularCrudApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AngularCrudApi.Endpoint;

/// <summary>
/// Dual-token authentication (template contract, see agents.md "Backend Contract"):
///   POST   /api/auth/register         { username, displayName, email, password, confirmPassword } -> 201 { accessToken, user }
///   POST   /api/auth/login            { username, password } -> { accessToken, user } + httpOnly refresh cookie
///   POST   /api/auth/refresh          (cookie) -> { accessToken } + rotated cookie
///   GET    /api/auth/me               (Bearer) -> user (roles, effective permissions, mustChangePassword)
///   POST   /api/auth/logout           (cookie) -> 204, session revoked
///   POST   /api/auth/change-password  (Bearer) { currentPassword, newPassword, confirmPassword } -> { accessToken, user }
///   GET    /api/auth/sessions         (Bearer) -> my active sessions
///   DELETE /api/auth/sessions/{id}    (Bearer) -> 204
/// Lockout: Security:MaxFailedLoginAttempts consecutive failures lock the account for Security:LockoutMinutes.
/// </summary>
public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    private static readonly PasswordHasher<AppUser> Hasher = new();

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("login", Login)
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithValidation<LoginRequest>();
        group.MapPost("register", Register)
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithValidation<RegisterRequest>();
        group.MapPost("refresh", Refresh).AllowAnonymous();
        group.MapPost("logout", Logout).AllowAnonymous();
        group.MapGet("me", Me).RequireAuthorization();
        group.MapPost("change-password", ChangePassword)
            .RequireAuthorization()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithValidation<ChangePasswordRequest>();
        group.MapGet("sessions", MySessions).RequireAuthorization();
        group.MapDelete("sessions/{sessionId}", RevokeMySession).RequireAuthorization();
    }

    // ------------------------------------------------------------------ login / signup

    private static async Task<IResult> Login(
        LoginRequest request, HttpContext http, AppDbContext db, TokenService tokens, AccessService access,
        AuditService audit, IOptions<JwtOptions> jwt, IOptions<SecurityOptions> security, CancellationToken ct)
    {
        var username = request.Username.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        var now = DateTime.UtcNow;

        if (user is not null && user.IsActive && user.IsLockedOut(now))
        {
            audit.Record(AuditActions.LoginFailed, "user", user.Id.ToString(), user.Username,
                new { reason = "locked" }, actorUserId: user.Id, actorName: user.DisplayName);
            await db.SaveChangesAsync(ct);
            return Locked(user.LockoutEndUtc!.Value, now);
        }

        var verification = user is { IsActive: true }
            ? Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password)
            : PasswordVerificationResult.Failed;

        if (user is null || verification == PasswordVerificationResult.Failed)
        {
            if (user is { IsActive: true })
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= Math.Max(1, security.Value.MaxFailedLoginAttempts))
                {
                    user.FailedLoginCount = 0;
                    user.LockoutEndUtc = now.AddMinutes(Math.Max(1, security.Value.LockoutMinutes));
                    audit.Record(AuditActions.LockedOut, "user", user.Id.ToString(), user.Username,
                        new { until = user.LockoutEndUtc }, actorUserId: user.Id, actorName: user.DisplayName);
                    await db.SaveChangesAsync(ct);
                    return Locked(user.LockoutEndUtc.Value, now);
                }
            }
            audit.Record(AuditActions.LoginFailed, "user", user?.Id.ToString(), user?.Username ?? username,
                new { reason = user is null ? "unknown user" : user.IsActive ? "wrong password" : "inactive" },
                actorUserId: user?.Id, actorName: user?.DisplayName);
            await db.SaveChangesAsync(ct);
            // Same answer for unknown user, wrong password and inactive account: no account enumeration.
            return Unauthorized("Invalid username or password");
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = Hasher.HashPassword(user, request.Password);
        }
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginAtUtc = now;
        audit.Record(AuditActions.LoginSucceeded, "user", user.Id.ToString(), user.Username,
            actorUserId: user.Id, actorName: user.DisplayName);

        var sessionId = await IssueRefreshCookieAsync(http, db, tokens, jwt.Value, user.Id, session: null, ct);
        var userAccess = await access.GetAsync(user.Id, ct);
        return Results.Ok(new LoginResponse(
            tokens.CreateAccessToken(user, userAccess, sessionId), AuthUserResponse.From(user, userAccess)));
    }

    /// <summary>
    /// Self-service signup: the account gets the Signup:DefaultRole (created on first use with
    /// Signup:DefaultRolePermissions) and is signed in immediately, like a successful login.
    /// </summary>
    private static async Task<IResult> Register(
        RegisterRequest request, HttpContext http, AppDbContext db, TokenService tokens, AccessService access,
        AuditService audit, IOptions<JwtOptions> jwt, IOptions<SignupOptions> signup, CancellationToken ct)
    {
        if (!signup.Value.Enabled) return ApiResults.NotFound("Self-service signup is disabled");

        var username = request.Username.Trim();
        var email = request.Email.Trim();
        var errors = new Dictionary<string, string[]>();
        if (!PasswordPolicy.IsStrong(request.Password)) errors["password"] = [PasswordPolicy.StrengthMessage];
        if (await db.Users.AnyAsync(u => u.Username == username, ct)) errors["username"] = ["This username is already taken"];
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) errors["email"] = ["An account with this email already exists"];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var role = await EnsureDefaultRoleAsync(db, signup.Value, ct);
        var now = DateTime.UtcNow;
        var user = new AppUser
        {
            Username = username,
            DisplayName = request.DisplayName.Trim(),
            Email = email,
            IsActive = true,
            CreatedAtUtc = now,
            LastLoginAtUtc = now,
            PasswordChangedAtUtc = now,
        };
        user.PasswordHash = Hasher.HashPassword(user, request.Password);
        if (role is not null) user.UserRoles.Add(new UserRole { Role = role });
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Lost a race with another signup for the same username (unique index UX_AppUsers_Username).
            return ApiResults.FieldError("username", "This username is already taken");
        }

        audit.Record(AuditActions.Signup, "user", user.Id.ToString(), user.Username,
            new { role = role?.Name }, actorUserId: user.Id, actorName: user.DisplayName);
        var sessionId = await IssueRefreshCookieAsync(http, db, tokens, jwt.Value, user.Id, session: null, ct);
        var userAccess = await access.GetAsync(user.Id, ct);
        return Results.Created("/api/auth/me", new LoginResponse(
            tokens.CreateAccessToken(user, userAccess, sessionId), AuthUserResponse.From(user, userAccess)));
    }

    private static async Task<Role?> EnsureDefaultRoleAsync(AppDbContext db, SignupOptions options, CancellationToken ct)
    {
        var name = options.DefaultRole.Trim();
        if (name.Length == 0) return null;
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == name, ct);
        if (role is not null) return role;

        role = new Role { Name = name, Description = "Default role of self-registered accounts" };
        foreach (var permission in options.DefaultRolePermissions
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(Permissions.IsGrantable)
                     .Distinct(StringComparer.Ordinal))
        {
            role.Permissions.Add(new RolePermission { Permission = permission });
        }
        db.Roles.Add(role);
        return role;
    }

    // ------------------------------------------------------------------ tokens

    private static async Task<IResult> Refresh(
        HttpContext http, AppDbContext db, TokenService tokens, AccessService access,
        SessionValidator validator, IOptions<JwtOptions> jwt, CancellationToken ct)
    {
        var settings = jwt.Value;
        var presented = http.Request.Cookies[settings.RefreshCookieName];
        if (string.IsNullOrEmpty(presented)) return Unauthorized("Refresh session expired");

        var hash = TokenService.Hash(presented);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = DateTime.UtcNow;

        if (stored is null || !stored.IsActive(now))
        {
            if (stored?.RevokedAtUtc is not null)
            {
                // A rotated (already used) token came back: assume theft and end every session of that user.
                // (Revoked sessions also land here; for them nothing is left to revoke.)
                var revoked = await db.RefreshTokens
                    .Where(t => t.UserId == stored.UserId && t.RevokedAtUtc == null)
                    .Select(t => t.SessionId).Distinct().ToListAsync(ct);
                await db.RefreshTokens
                    .Where(t => t.UserId == stored.UserId && t.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
                foreach (var id in revoked) validator.InvalidateSession(id);
            }
            ClearRefreshCookie(http, settings);
            return Unauthorized("Refresh session expired");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId && u.IsActive, ct);
        if (user is null)
        {
            ClearRefreshCookie(http, settings);
            return Unauthorized("Refresh session expired");
        }

        stored.RevokedAtUtc = now; // rotation: every refresh token is single-use; the session continues
        var sessionId = await IssueRefreshCookieAsync(http, db, tokens, settings, user.Id, stored, ct);
        var userAccess = await access.GetAsync(user.Id, ct);
        return Results.Ok(new RefreshResponse(tokens.CreateAccessToken(user, userAccess, sessionId)));
    }

    private static async Task<IResult> Logout(
        HttpContext http, AppDbContext db, SessionValidator validator, IOptions<JwtOptions> jwt, CancellationToken ct)
    {
        var settings = jwt.Value;
        var presented = http.Request.Cookies[settings.RefreshCookieName];
        if (!string.IsNullOrEmpty(presented))
        {
            var hash = TokenService.Hash(presented);
            var token = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (token is not null)
            {
                var now = DateTime.UtcNow;
                await db.RefreshTokens
                    .Where(t => t.SessionId == token.SessionId && t.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
                validator.InvalidateSession(token.SessionId);
            }
        }
        ClearRefreshCookie(http, settings);
        return Results.NoContent();
    }

    private static async Task<IResult> Me(HttpContext http, AppDbContext db, AccessService access, CancellationToken ct)
    {
        if (CurrentUserId(http) is not { } userId) return Unauthorized("Invalid access token");
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (user is null) return Unauthorized("Unknown user");
        return Results.Ok(AuthUserResponse.From(user, await access.GetAsync(userId, ct)));
    }

    // ------------------------------------------------------------------ my account

    /// <summary>
    /// Changes the password (also completes a forced change after an administrator reset). Every OTHER session
    /// is signed out; this one continues with the returned access token.
    /// </summary>
    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest request, HttpContext http, AppDbContext db, TokenService tokens, AccessService access,
        AuditService audit, SessionManager sessions, SessionValidator validator, CancellationToken ct)
    {
        if (CurrentUserId(http) is not { } userId) return Unauthorized("Invalid access token");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (user is null) return Unauthorized("Unknown user");

        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            return ApiResults.FieldError("currentPassword", "Current password is incorrect");
        }
        if (!PasswordPolicy.IsStrong(request.NewPassword)) return ApiResults.FieldError("newPassword", PasswordPolicy.StrengthMessage);
        if (request.NewPassword == request.CurrentPassword)
        {
            return ApiResults.FieldError("newPassword", "Choose a password different from the current one");
        }

        var now = DateTime.UtcNow;
        var forced = user.MustChangePassword;
        user.PasswordHash = Hasher.HashPassword(user, request.NewPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAtUtc = now;
        user.RotateSecurityStamp();
        audit.Record(AuditActions.PasswordChanged, "user", user.Id.ToString(), user.Username, new { forced });
        await db.SaveChangesAsync(ct);
        validator.InvalidateUser(user.Id);

        var currentSession = CurrentSessionId(http);
        var signedOut = await sessions.RevokeAllAsync(user.Id, currentSession, ct);
        var userAccess = await access.GetAsync(user.Id, ct);
        var sessionId = currentSession ?? Guid.Empty;
        return Results.Ok(new
        {
            accessToken = tokens.CreateAccessToken(user, userAccess, sessionId),
            user = AuthUserResponse.From(user, userAccess),
            otherSessionsSignedOut = signedOut,
        });
    }

    private static async Task<IResult> MySessions(HttpContext http, SessionManager sessions, CancellationToken ct)
    {
        if (CurrentUserId(http) is not { } userId) return Unauthorized("Invalid access token");
        return Results.Ok(await sessions.ListAsync(userId, CurrentSessionId(http), ct));
    }

    private static async Task<IResult> RevokeMySession(
        string sessionId, HttpContext http, AppDbContext db, SessionManager sessions, AuditService audit, CancellationToken ct)
    {
        if (CurrentUserId(http) is not { } userId) return Unauthorized("Invalid access token");
        if (!Guid.TryParse(sessionId, out var id) || !await sessions.RevokeAsync(userId, id, ct))
        {
            return ApiResults.NotFound("Session not found");
        }
        audit.Record(AuditActions.SessionRevoked, "user", userId.ToString(), http.User.FindFirst(AppClaims.Name)?.Value,
            new { session = id.ToString("N") });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ helpers

    internal static int? CurrentUserId(HttpContext http) =>
        int.TryParse(http.User.FindFirst(AppClaims.Subject)?.Value, out var id) ? id : null;

    internal static Guid? CurrentSessionId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst(AppClaims.SessionId)?.Value, out var id) ? id : null;

    /// <summary>
    /// Issues a refresh token (new session, or the next link of <paramref name="session"/>'s chain), stores it
    /// with device info and sets the cookie. Returns the session id.
    /// </summary>
    private static async Task<Guid> IssueRefreshCookieAsync(
        HttpContext http, AppDbContext db, TokenService tokens, JwtOptions settings, int userId,
        RefreshToken? session, CancellationToken ct)
    {
        var token = TokenService.CreateRefreshToken();
        var now = DateTime.UtcNow;
        var expires = now.Add(tokens.RefreshTokenLifetime);
        var sessionId = session?.SessionId ?? Guid.NewGuid();
        var userAgent = http.Request.Headers.UserAgent.ToString();

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = TokenService.Hash(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = expires,
            SessionId = sessionId,
            SessionStartedAtUtc = session?.SessionStartedAtUtc ?? now,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = userAgent.Length > 256 ? userAgent[..256] : userAgent,
        });
        // Housekeeping: drop this user's expired tokens.
        await db.RefreshTokens.Where(t => t.UserId == userId && t.ExpiresAtUtc < now).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);

        http.Response.Cookies.Append(settings.RefreshCookieName, token, BuildCookieOptions(settings, expires));
        return sessionId;
    }

    private static void ClearRefreshCookie(HttpContext http, JwtOptions settings) =>
        http.Response.Cookies.Delete(settings.RefreshCookieName, BuildCookieOptions(settings, expires: null));

    private static CookieOptions BuildCookieOptions(JwtOptions settings, DateTime? expires) => new()
    {
        HttpOnly = true,
        // Browsers accept Secure cookies from http://localhost, so this stays on in Development too.
        Secure = true,
        SameSite = settings.RefreshCookieSameSite,
        Path = settings.RefreshCookiePath,
        Expires = expires,
        IsEssential = true,
    };

    private static IResult Locked(DateTime until, DateTime now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));
        return Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Account temporarily locked",
            detail: $"Too many failed sign-in attempts. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
    }

    private static IResult Unauthorized(string title) =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: title);
}
