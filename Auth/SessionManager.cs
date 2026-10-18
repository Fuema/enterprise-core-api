using AngularCrudApi.Data;
using AngularCrudApi.Models;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Auth;

/// <summary>
/// Sessions = chains of rotated refresh tokens sharing a SessionId (one per sign-in on a device/browser).
/// Listing and revoking them, plus the cache evictions that make a revocation effective on the next request.
/// </summary>
public sealed class SessionManager(AppDbContext db, SessionValidator validator)
{
    /// <summary>Active sessions of a user, most recently used first.</summary>
    public async Task<SessionResponse[]> ListAsync(int userId, Guid? currentSessionId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null && t.ExpiresAtUtc > now)
            .OrderByDescending(t => t.CreatedAtUtc)
            .ToListAsync(ct);

        // Normally one active token per session; keep the newest if a race left two.
        return tokens
            .GroupBy(t => t.SessionId)
            .Select(g => g.First())
            .Select(t => new SessionResponse(
                t.SessionId.ToString("N"), t.SessionStartedAtUtc, t.CreatedAtUtc, t.ExpiresAtUtc,
                t.IpAddress, t.UserAgent, t.SessionId == currentSessionId))
            .ToArray();
    }

    /// <summary>Revokes one session of the user. False when it does not exist or is already over.</summary>
    public async Task<bool> RevokeAsync(int userId, Guid sessionId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var revoked = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.SessionId == sessionId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
        validator.InvalidateSession(sessionId);
        return revoked > 0;
    }

    /// <summary>Revokes every session of the user except <paramref name="keepSessionId"/>. Returns how many ended.</summary>
    public async Task<int> RevokeAllAsync(int userId, Guid? keepSessionId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sessionIds = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null && t.SessionId != keepSessionId)
            .Select(t => t.SessionId)
            .Distinct()
            .ToListAsync(ct);
        if (sessionIds.Count == 0) return 0;

        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null && sessionIds.Contains(t.SessionId))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
        foreach (var id in sessionIds) validator.InvalidateSession(id);
        return sessionIds.Count;
    }
}
