using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Infrastructure;
using AngularCrudApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Endpoint;

/// <summary>
/// Account administration (the admin screen):
///   GET /api/users?page=&amp;pageSize=&amp;search=&amp;sort=&amp;isActive=   users:read
///   GET /api/users/{id}                                         users:read
///   GET /api/users/permissions -> { scopes }                    users:read
///   PUT /api/users/{id} { displayName, email, roles, permissions, isActive } -> 200 + user   users:write
/// Accounts are created by signup (POST /api/auth/register); they are deactivated, never deleted.
/// Guards: an admin cannot deactivate themselves or remove their own users:write (no lock-out).
/// Deactivating an account revokes its refresh tokens: the user is signed out at the next refresh
/// (access tokens live at most Jwt:AccessTokenMinutes). Permission changes apply from the next token.
/// </summary>
public static class UserEndpoints
{
    private const int MaxRoleLength = 50;

    private static readonly SortMap<AppUser> Sorts = new SortMap<AppUser>()
        .Add("id", u => u.Id)
        .Add("username", u => u.Username)
        .Add("displayName", u => u.DisplayName)
        .Add("email", u => u.Email)
        .Add("isActive", u => u.IsActive)
        .Add("createdAtUtc", u => u.CreatedAtUtc);

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(Permissions.UsersRead);

        group.MapGet("", GetPage);
        group.MapGet("permissions", () => Results.Ok(new PermissionCatalogResponse(Permissions.All)));
        group.MapGet("{id:int}", GetById);
        group.MapPut("{id:int}", Update).RequireAuthorization(Permissions.UsersWrite).WithValidation<UserUpdateRequest>();
    }

    private static async Task<IResult> GetPage(
        [AsParameters] PageRequest paging, bool? isActive, AppDbContext db, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        foreach (var term in paging.SearchTerms)
        {
            query = query.Where(u =>
                u.Username.Contains(term) || u.DisplayName.Contains(term) ||
                (u.Email != null && u.Email.Contains(term)) || u.Roles.Contains(term));
        }
        if (isActive is not null) query = query.Where(u => u.IsActive == isActive);

        var page = await Sorts.Apply(query, paging.Sort, "username.asc").ThenBy(u => u.Id).ToPagedAsync(paging, ct);
        // Map after paging: roles/permissions are stored as comma-separated text.
        return Results.Ok(new PagedResponse<UserResponse>(
            page.Data.Select(UserResponse.From).ToList(), page.TotalCount, page.PageSize, page.CurrentPage));
    }

    private static async Task<IResult> GetById(int id, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? NotFound() : Results.Ok(UserResponse.From(user));
    }

    private static async Task<IResult> Update(
        int id, [FromBody] UserUpdateRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        var roles = Normalize(request.Roles);
        var permissions = Normalize(request.Permissions);
        var errors = new Dictionary<string, string[]>();

        var invalidRole = roles.FirstOrDefault(r => r.Length > MaxRoleLength || r.Contains(','));
        if (invalidRole is not null) errors["roles"] = [$"Invalid role \"{invalidRole}\" (max {MaxRoleLength} characters, no commas)"];

        var unknown = permissions.Where(p => !Permissions.IsGrantable(p)).ToArray();
        if (unknown.Length > 0) errors["permissions"] = [$"Unknown permission: {string.Join(", ", unknown)}"];

        if (email is not null && await db.Users.AnyAsync(u => u.Email == email && u.Id != id, ct))
        {
            errors["email"] = ["An account with this email already exists"];
        }

        // Lock-out protection: the signed-in admin keeps an active account with users:write.
        var isSelf = int.TryParse(http.User.FindFirst(AppClaims.Subject)?.Value, out var currentUserId) && currentUserId == id;
        if (isSelf && !request.IsActive) errors["isActive"] = ["You cannot deactivate your own account"];
        if (isSelf && !Permissions.Satisfies(permissions, Permissions.UsersWrite))
        {
            errors["permissions"] = ["You cannot remove your own users:write permission"];
        }

        var rolesText = string.Join(',', roles);
        var permissionsText = string.Join(',', permissions);
        if (rolesText.Length > 400) errors["roles"] = ["Too many roles (400 characters in total)"];
        if (permissionsText.Length > 1000) errors["permissions"] = ["Too many permissions (1000 characters in total)"];

        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var deactivated = user.IsActive && !request.IsActive;
        user.DisplayName = request.DisplayName.Trim();
        user.Email = email;
        user.Roles = rolesText;
        user.Permissions = permissionsText;
        user.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);

        if (deactivated)
        {
            var now = DateTime.UtcNow;
            await db.RefreshTokens
                .Where(t => t.UserId == id && t.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
        }

        return Results.Ok(UserResponse.From(user));
    }

    /// <summary>Trimmed, non-empty, distinct (ordinal), in the given order.</summary>
    private static string[] Normalize(IEnumerable<string?> values) =>
        values.Select(v => v?.Trim() ?? string.Empty).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private static IResult NotFound() => ApiResults.NotFound("User not found");
}
