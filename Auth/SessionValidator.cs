using AngularCrudApi.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AngularCrudApi.Auth;

/// <summary>
/// Makes access changes take effect on the user's NEXT request instead of when the 15-minute token expires.
/// Runs after the JWT signature/lifetime checks (JwtBearerEvents.OnTokenValidated) and rejects the token when:
///   - the account is inactive or gone,
///   - the token's security stamp is stale (roles, grants, password or status changed), or
///   - its session (refresh-token chain) was revoked or signed out.
/// The client then calls /auth/refresh: an active account gets a new token with its current permissions;
/// a revoked session or deactivated account ends up on the login page.
/// Answers are cached briefly; every change made through the API evicts the affected entries immediately.
/// </summary>
public sealed class SessionValidator(IServiceScopeFactory scopes, IMemoryCache cache, IOptions<SecurityOptions> options)
{
    private sealed record UserState(string SecurityStamp, bool IsActive);

    private TimeSpan Ttl => TimeSpan.FromSeconds(Math.Max(0, options.Value.ValidationCacheSeconds));

    public async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal is null || !int.TryParse(principal.FindFirst(AppClaims.Subject)?.Value, out var userId))
        {
            context.Fail("Invalid subject.");
            return;
        }

        var ct = context.HttpContext.RequestAborted;
        var state = await GetUserStateAsync(userId, ct);
        var stamp = principal.FindFirst(AppClaims.SecurityStamp)?.Value;
        if (state is null || !state.IsActive || !string.Equals(state.SecurityStamp, stamp, StringComparison.Ordinal))
        {
            context.Fail("Access changed; refresh the token.");
            return;
        }

        if (Guid.TryParse(principal.FindFirst(AppClaims.SessionId)?.Value, out var sessionId) &&
            !await IsSessionActiveAsync(sessionId, ct))
        {
            context.Fail("Session ended.");
        }
    }

    public void InvalidateUser(int userId) => cache.Remove(UserKey(userId));

    public void InvalidateSession(Guid sessionId) => cache.Remove(SessionKey(sessionId));

    private async Task<UserState?> GetUserStateAsync(int userId, CancellationToken ct)
    {
        if (cache.TryGetValue(UserKey(userId), out UserState? cached)) return cached;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserState(u.SecurityStamp, u.IsActive))
            .FirstOrDefaultAsync(ct);
        cache.Set(UserKey(userId), state, Ttl);
        return state;
    }

    private async Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken ct)
    {
        if (cache.TryGetValue(SessionKey(sessionId), out bool cached)) return cached;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var active = await db.RefreshTokens.AsNoTracking()
            .AnyAsync(t => t.SessionId == sessionId && t.RevokedAtUtc == null && t.ExpiresAtUtc > now, ct);
        cache.Set(SessionKey(sessionId), active, Ttl);
        return active;
    }

    private static string UserKey(int userId) => $"access:user:{userId}";
    private static string SessionKey(Guid sessionId) => $"access:session:{sessionId:N}";
}
