using AngularCrudApi.Data;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Auth;

/// <summary>Roles and effective permissions of one user.</summary>
public sealed record UserAccess(string[] Roles, string[] Permissions)
{
    public static readonly UserAccess None = new([], []);
}

/// <summary>
/// Single place that answers "what may this user do?":
///   effective permissions = permissions of every assigned role  UNION  the user's direct grants.
/// Used for tokens, /auth/me and the admin screens.
/// </summary>
public sealed class AccessService(AppDbContext db)
{
    public async Task<UserAccess> GetAsync(int userId, CancellationToken ct = default)
    {
        var roles = await db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => new { ur.Role.Name, Permissions = ur.Role.Permissions.Select(p => p.Permission).ToList() })
            .ToListAsync(ct);
        var direct = await db.UserPermissions
            .Where(p => p.UserId == userId)
            .Select(p => p.Permission)
            .ToListAsync(ct);

        return new UserAccess(
            roles.Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Effective(roles.SelectMany(r => r.Permissions).Concat(direct)));
    }

    /// <summary>Distinct, "*" first, then alphabetical.</summary>
    public static string[] Effective(IEnumerable<string> permissions) =>
        permissions
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p == "*" ? 0 : 1)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Ids of active users that hold the Administrator system role.</summary>
    public Task<List<int>> ActiveAdministratorIdsAsync(CancellationToken ct = default) =>
        db.UserRoles
            .Where(ur => ur.Role.IsSystem && ur.Role.Name == Models.Role.AdministratorName && ur.User.IsActive)
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(ct);
}
