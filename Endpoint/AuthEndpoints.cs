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
///   POST /api/auth/register { username, displayName, email, password, confirmPassword } -> 201 { accessToken, user } + cookie
///   POST /api/auth/login    { username, password } -> { accessToken, user } + httpOnly refresh cookie
///   POST /api/auth/refresh  (cookie)               -> { accessToken }        + rotated cookie
///   GET  /api/auth/me       (Bearer)               -> user
///   POST /api/auth/logout   (cookie)               -> 204, cookie revoked and cleared
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
    }

    private static async Task<IResult> Login(
        LoginRequest request, HttpContext http, AppDbContext db, TokenService tokens,
        IOptions<JwtOptions> options, CancellationToken ct)
    {
        var username = request.Username.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

        var verification = user is { IsActive: true }
            ? Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password)
            : PasswordVerificationResult.Failed;
        if (user is null || verification == PasswordVerificationResult.Failed)
        {
            // Same answer for "unknown user" and "wrong password": no account enumeration.
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid username or password");
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = Hasher.HashPassword(user, request.Password);
        }

        await IssueRefreshCookieAsync(http, db, tokens, options.Value, user.Id, ct);
        return Results.Ok(new LoginResponse(tokens.CreateAccessToken(user), AuthUserResponse.From(user)));
    }

    /// <summary>
    /// Self-service signup. The account gets Signup:DefaultRoles / Signup:DefaultPermissions (least privilege)
    /// and is signed in immediately, exactly like a successful login.
    /// </summary>
    private static async Task<IResult> Register(
        RegisterRequest request, HttpContext http, AppDbContext db, TokenService tokens,
        IOptions<JwtOptions> options, IOptions<SignupOptions> signup, CancellationToken ct)
    {
        if (!signup.Value.Enabled)
        {
            return ApiResults.NotFound("Self-service signup is disabled");
        }

        var username = request.Username.Trim();
        var email = request.Email.Trim();
        var errors = new Dictionary<string, string[]>();
        if (!IsStrongPassword(request.Password))
        {
            errors["password"] = ["Include an uppercase letter, a lowercase letter and a digit"];
        }
        if (await db.Users.AnyAsync(u => u.Username == username, ct))
        {
            errors["username"] = ["This username is already taken"];
        }
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            errors["email"] = ["An account with this email already exists"];
        }
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var user = new AppUser
        {
            Username = username,
            DisplayName = request.DisplayName.Trim(),
            Email = email,
            Roles = signup.Value.DefaultRoles,
            Permissions = signup.Value.DefaultPermissions,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        user.PasswordHash = Hasher.HashPassword(user, request.Password);
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

        await IssueRefreshCookieAsync(http, db, tokens, options.Value, user.Id, ct);
        return Results.Created("/api/auth/me", new LoginResponse(tokens.CreateAccessToken(user), AuthUserResponse.From(user)));
    }

    private static bool IsStrongPassword(string password) =>
        password.Any(char.IsUpper) && password.Any(char.IsLower) && password.Any(char.IsDigit);

    private static async Task<IResult> Refresh(
        HttpContext http, AppDbContext db, TokenService tokens, IOptions<JwtOptions> options, CancellationToken ct)
    {
        var settings = options.Value;
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
                await db.RefreshTokens
                    .Where(t => t.UserId == stored.UserId && t.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
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

        stored.RevokedAtUtc = now; // rotation: every refresh token is single-use
        await IssueRefreshCookieAsync(http, db, tokens, settings, user.Id, ct);
        return Results.Ok(new RefreshResponse(tokens.CreateAccessToken(user)));
    }

    private static async Task<IResult> Logout(HttpContext http, AppDbContext db, IOptions<JwtOptions> options, CancellationToken ct)
    {
        var settings = options.Value;
        var presented = http.Request.Cookies[settings.RefreshCookieName];
        if (!string.IsNullOrEmpty(presented))
        {
            var hash = TokenService.Hash(presented);
            var now = DateTime.UtcNow;
            await db.RefreshTokens
                .Where(t => t.TokenHash == hash && t.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
        }
        ClearRefreshCookie(http, settings);
        return Results.NoContent();
    }

    private static async Task<IResult> Me(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!int.TryParse(http.User.FindFirst(AppClaims.Subject)?.Value, out var userId))
        {
            return Unauthorized("Invalid access token");
        }
        // Read from the database so role/permission changes apply without waiting for token expiry.
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        return user is null ? Unauthorized("Unknown user") : Results.Ok(AuthUserResponse.From(user));
    }

    // -------------------------------------------------------------------------

    private static async Task IssueRefreshCookieAsync(
        HttpContext http, AppDbContext db, TokenService tokens, JwtOptions settings, int userId, CancellationToken ct)
    {
        var token = TokenService.CreateRefreshToken();
        var now = DateTime.UtcNow;
        var expires = now.Add(tokens.RefreshTokenLifetime);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = TokenService.Hash(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = expires,
        });
        // Housekeeping: drop this user's expired tokens.
        await db.RefreshTokens.Where(t => t.UserId == userId && t.ExpiresAtUtc < now).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);

        http.Response.Cookies.Append(settings.RefreshCookieName, token, BuildCookieOptions(settings, expires));
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

    private static IResult Unauthorized(string title) =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: title);
}
