using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Infrastructure;
using AngularCrudApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AngularCrudApi.Endpoint;

/// <summary>
/// Account administration:
///   GET    /api/users?page=&amp;pageSize=&amp;search=&amp;sort=&amp;isActive=&amp;roleId=   users:read
///   GET    /api/users/{id}                                   users:read
///   GET    /api/users/role-options -> [{ id, name, isSystem, permissions }]   users:read
///   GET    /api/users/{id}/sessions                          users:read
///   POST   /api/users { username, displayName, email, roleIds, directPermissions } -> 201 { user, temporaryPassword }   users:write
///   PUT    /api/users/{id} { displayName, email, roleIds, directPermissions, isActive } -> 200 user   users:write
///   POST   /api/users/{id}/reset-password -> { temporaryPassword }   users:write
///   POST   /api/users/{id}/unlock -> 204                     users:write
///   DELETE /api/users/{id}/sessions[/{sessionId}] -> 204     users:write
/// Accounts are deactivated, never deleted (audit history keeps pointing at them).
/// Guards: an admin cannot deactivate themselves or lose users:write; at least one active user keeps the
/// Administrator role. Any change to access rotates the user's security stamp (effective on the next request).
/// </summary>
public static class UserEndpoints
{
    private static readonly PasswordHasher<AppUser> Hasher = new();

    private static readonly SortMap<AppUser> Sorts = new SortMap<AppUser>()
        .Add("id", u => u.Id)
        .Add("username", u => u.Username)
        .Add("displayName", u => u.DisplayName)
        .Add("email", u => u.Email)
        .Add("isActive", u => u.IsActive)
        .Add("lastLoginAtUtc", u => u.LastLoginAtUtc)
        .Add("createdAtUtc", u => u.CreatedAtUtc);

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(Permissions.UsersRead);

        group.MapGet("", GetPage);
        group.MapGet("role-options", RoleOptions);
        group.MapGet("{id:int}", GetById);
        group.MapGet("{id:int}/sessions", Sessions);

        group.MapPost("", Create).RequireAuthorization(Permissions.UsersWrite).WithValidation<UserCreateRequest>();
        group.MapPut("{id:int}", Update).RequireAuthorization(Permissions.UsersWrite).WithValidation<UserUpdateRequest>();
        group.MapPost("{id:int}/reset-password", ResetPassword).RequireAuthorization(Permissions.UsersWrite);
        group.MapPost("{id:int}/unlock", Unlock).RequireAuthorization(Permissions.UsersWrite);
        group.MapDelete("{id:int}/sessions", RevokeAllSessions).RequireAuthorization(Permissions.UsersWrite);
        group.MapDelete("{id:int}/sessions/{sessionId}", RevokeSession).RequireAuthorization(Permissions.UsersWrite);
    }

    // ------------------------------------------------------------------ queries

    private static async Task<IResult> GetPage(
        [AsParameters] PageRequest paging, bool? isActive, int? roleId, AppDbContext db, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        foreach (var term in paging.SearchTerms)
        {
            query = query.Where(u =>
                u.Username.Contains(term) || u.DisplayName.Contains(term) ||
                (u.Email != null && u.Email.Contains(term)) ||
                u.UserRoles.Any(ur => ur.Role.Name.Contains(term)));
        }
        if (isActive is not null) query = query.Where(u => u.IsActive == isActive);
        if (roleId is > 0) query = query.Where(u => u.UserRoles.Any(ur => ur.RoleId == roleId));

        var page = await Sorts.Apply(query, paging.Sort, "username.asc").ThenBy(u => u.Id)
            .Select(u => new UserListItem
            {
                Id = u.Id,
                Username = u.Username,
                DisplayName = u.DisplayName,
                Email = u.Email,
                IsActive = u.IsActive,
                LockoutEndUtc = u.LockoutEndUtc,
                MustChangePassword = u.MustChangePassword,
                LastLoginAtUtc = u.LastLoginAtUtc,
                CreatedAtUtc = u.CreatedAtUtc,
                Roles = u.UserRoles.OrderBy(ur => ur.Role.Name).Select(ur => new RoleRef(ur.Role.Id, ur.Role.Name, ur.Role.IsSystem)).ToList(),
                DirectPermissionCount = u.DirectPermissions.Count,
            })
            .ToPagedAsync(paging, ct);
        return Results.Ok(page);
    }

    private static async Task<IResult> GetById(int id, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var detail = await LoadDetailAsync(db, access, id, ct);
        return detail is null ? NotFound() : Results.Ok(detail);
    }

    private static async Task<IResult> RoleOptions(AppDbContext db, CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking()
            .OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name)
            .Select(r => new { r.Id, r.Name, r.IsSystem, Permissions = r.Permissions.Select(p => p.Permission).ToList() })
            .ToListAsync(ct);
        return Results.Ok(roles.Select(r => new RoleOption(r.Id, r.Name, r.IsSystem, AccessService.Effective(r.Permissions))));
    }

    private static async Task<IResult> Sessions(int id, AppDbContext db, SessionManager sessions, HttpContext http, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id, ct)) return NotFound();
        var current = AuthEndpoints.CurrentUserId(http) == id ? AuthEndpoints.CurrentSessionId(http) : null;
        return Results.Ok(await sessions.ListAsync(id, current, ct));
    }

    // ------------------------------------------------------------------ commands

    private static async Task<IResult> Create(
        [FromBody] UserCreateRequest request, AppDbContext db, AccessService access, AuditService audit,
        IOptions<SecurityOptions> security, CancellationToken ct)
    {
        var username = request.Username.Trim();
        var email = NormalizeEmail(request.Email);
        var permissions = Normalize(request.DirectPermissions);
        var errors = new Dictionary<string, string[]>();

        if (await db.Users.AnyAsync(u => u.Username == username, ct)) errors["username"] = ["This username is already taken"];
        if (email is not null && await db.Users.AnyAsync(u => u.Email == email, ct)) errors["email"] = ["An account with this email already exists"];
        var roles = await LoadRolesAsync(db, request.RoleIds, errors, ct);
        ValidatePermissions(permissions, "directPermissions", errors);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var temporaryPassword = PasswordPolicy.GenerateTemporary(security.Value.TemporaryPasswordLength);
        var user = new AppUser
        {
            Username = username,
            DisplayName = request.DisplayName.Trim(),
            Email = email,
            IsActive = true,
            MustChangePassword = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        user.PasswordHash = Hasher.HashPassword(user, temporaryPassword);
        foreach (var role in roles) user.UserRoles.Add(new UserRole { RoleId = role.Id });
        foreach (var permission in permissions) user.DirectPermissions.Add(new UserPermission { Permission = permission });
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return ApiResults.FieldError("username", "This username is already taken");
        }

        audit.Record(AuditActions.UserCreated, "user", user.Id.ToString(), user.Username,
            new { roles = roles.Select(r => r.Name), directPermissions = permissions });
        await db.SaveChangesAsync(ct);

        var detail = (await LoadDetailAsync(db, access, user.Id, ct))!;
        return Results.Created($"/api/users/{user.Id}", new UserCreatedResponse(detail, temporaryPassword));
    }

    private static async Task<IResult> Update(
        int id, [FromBody] UserUpdateRequest request, HttpContext http, AppDbContext db, AccessService access,
        AuditService audit, SessionManager sessions, SessionValidator validator, CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Include(u => u.DirectPermissions)
            .FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var email = NormalizeEmail(request.Email);
        var permissions = Normalize(request.DirectPermissions);
        var errors = new Dictionary<string, string[]>();

        if (email is not null && await db.Users.AnyAsync(u => u.Email == email && u.Id != id, ct))
        {
            errors["email"] = ["An account with this email already exists"];
        }
        var roles = await LoadRolesAsync(db, request.RoleIds, errors, ct);
        ValidatePermissions(permissions, "directPermissions", errors);

        // Lock-out protection.
        var isSelf = AuthEndpoints.CurrentUserId(http) == id;
        if (isSelf && !request.IsActive) errors["isActive"] = ["You cannot deactivate your own account"];
        if (isSelf && errors.Count == 0)
        {
            var effective = roles.SelectMany(r => r.Permissions.Select(p => p.Permission)).Concat(permissions);
            if (!Permissions.Satisfies(effective, Permissions.UsersWrite))
            {
                errors["directPermissions"] = ["You cannot remove your own users:write permission"];
            }
        }
        var wasAdmin = user.IsActive && user.UserRoles.Any(ur => IsAdministrator(ur.Role));
        var staysAdmin = request.IsActive && roles.Any(IsAdministrator);
        if (wasAdmin && !staysAdmin && errors.Count == 0)
        {
            var admins = await access.ActiveAdministratorIdsAsync(ct);
            if (admins.All(adminId => adminId == id))
            {
                errors["roleIds"] = ["At least one active user must keep the Administrator role"];
            }
        }
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        // Apply + describe the change for the audit log.
        var changes = new Dictionary<string, object?>();
        var displayName = request.DisplayName.Trim();
        if (user.DisplayName != displayName) changes["displayName"] = new[] { user.DisplayName, displayName };
        if (user.Email != email) changes["email"] = new[] { user.Email, email };

        var oldRoles = user.UserRoles.Select(ur => ur.Role.Name).ToHashSet();
        var newRoles = roles.Select(r => r.Name).ToHashSet();
        if (!oldRoles.SetEquals(newRoles))
        {
            changes["roles"] = new { added = newRoles.Except(oldRoles), removed = oldRoles.Except(newRoles) };
        }
        var oldPermissions = user.DirectPermissions.Select(p => p.Permission).ToHashSet();
        if (!oldPermissions.SetEquals(permissions))
        {
            changes["directPermissions"] = new { added = permissions.Except(oldPermissions), removed = oldPermissions.Except(permissions) };
        }
        if (user.IsActive != request.IsActive) changes["isActive"] = new[] { user.IsActive, request.IsActive };

        var accessChanged = changes.ContainsKey("roles") || changes.ContainsKey("directPermissions") || changes.ContainsKey("isActive");
        var deactivated = user.IsActive && !request.IsActive;

        user.DisplayName = displayName;
        user.Email = email;
        user.IsActive = request.IsActive;
        SyncRoles(user, roles);
        SyncPermissions(user, permissions);
        if (accessChanged) user.RotateSecurityStamp();
        if (changes.Count > 0) audit.Record(AuditActions.UserUpdated, "user", user.Id.ToString(), user.Username, new { changes });

        await db.SaveChangesAsync(ct);
        validator.InvalidateUser(id);
        if (deactivated) await sessions.RevokeAllAsync(id, keepSessionId: null, ct);

        return Results.Ok(await LoadDetailAsync(db, access, id, ct));
    }

    /// <summary>
    /// Generates a temporary password, forces a change at next sign-in and signs the user out everywhere.
    /// The password is returned once and never stored in clear text.
    /// </summary>
    private static async Task<IResult> ResetPassword(
        int id, HttpContext http, AppDbContext db, AuditService audit, SessionManager sessions,
        SessionValidator validator, IOptions<SecurityOptions> security, CancellationToken ct)
    {
        if (AuthEndpoints.CurrentUserId(http) == id)
        {
            return ApiResults.Conflict("Use change password", "To change your own password, use your account page.");
        }
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var temporaryPassword = PasswordPolicy.GenerateTemporary(security.Value.TemporaryPasswordLength);
        user.PasswordHash = Hasher.HashPassword(user, temporaryPassword);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.RotateSecurityStamp();
        audit.Record(AuditActions.UserPasswordReset, "user", user.Id.ToString(), user.Username);
        await db.SaveChangesAsync(ct);
        validator.InvalidateUser(id);
        await sessions.RevokeAllAsync(id, keepSessionId: null, ct);

        return Results.Ok(new TemporaryPasswordResponse(temporaryPassword));
    }

    private static async Task<IResult> Unlock(int id, AppDbContext db, AuditService audit, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        audit.Record(AuditActions.UserUnlocked, "user", user.Id.ToString(), user.Username);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeAllSessions(
        int id, AppDbContext db, AuditService audit, SessionManager sessions, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        var count = await sessions.RevokeAllAsync(id, keepSessionId: null, ct);
        audit.Record(AuditActions.UserSessionsRevoked, "user", user.Id.ToString(), user.Username, new { sessions = count });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeSession(
        int id, string sessionId, AppDbContext db, AuditService audit, SessionManager sessions, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        if (!Guid.TryParse(sessionId, out var session) || !await sessions.RevokeAsync(id, session, ct))
        {
            return ApiResults.NotFound("Session not found");
        }
        audit.Record(AuditActions.UserSessionsRevoked, "user", user.Id.ToString(), user.Username,
            new { sessions = 1, session = session.ToString("N") });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ helpers

    internal static async Task<UserDetailResponse?> LoadDetailAsync(AppDbContext db, AccessService access, int id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Include(u => u.DirectPermissions)
            .FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return null;

        var effective = await access.GetAsync(id, ct);
        return new UserDetailResponse(
            user.Id, user.Username, user.DisplayName, user.Email, user.IsActive, user.LockoutEndUtc,
            user.FailedLoginCount, user.MustChangePassword, user.LastLoginAtUtc, user.PasswordChangedAtUtc,
            user.CreatedAtUtc,
            user.UserRoles.Select(ur => new RoleRef(ur.Role.Id, ur.Role.Name, ur.Role.IsSystem)).OrderBy(r => r.Name).ToArray(),
            AccessService.Effective(user.DirectPermissions.Select(p => p.Permission)),
            effective.Permissions);
    }

    /// <summary>Roles by id (with their permissions); unknown ids become a field error.</summary>
    private static async Task<List<Role>> LoadRolesAsync(
        AppDbContext db, IEnumerable<int> roleIds, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var ids = roleIds.Distinct().ToList();
        var roles = await db.Roles.Include(r => r.Permissions).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        if (roles.Count != ids.Count) errors["roleIds"] = ["One or more roles no longer exist"];
        return roles;
    }

    internal static void ValidatePermissions(string[] permissions, string field, Dictionary<string, string[]> errors)
    {
        var unknown = permissions.Where(p => !Permissions.IsGrantable(p)).ToArray();
        if (unknown.Length > 0) errors[field] = [$"Unknown permission: {string.Join(", ", unknown)}"];
    }

    private static void SyncRoles(AppUser user, List<Role> roles)
    {
        var wanted = roles.Select(r => r.Id).ToHashSet();
        foreach (var link in user.UserRoles.Where(ur => !wanted.Contains(ur.RoleId)).ToList()) user.UserRoles.Remove(link);
        foreach (var roleId in wanted.Where(rid => user.UserRoles.All(ur => ur.RoleId != rid)).ToList())
        {
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
        }
    }

    private static void SyncPermissions(AppUser user, string[] permissions)
    {
        foreach (var grant in user.DirectPermissions.Where(p => !permissions.Contains(p.Permission)).ToList())
        {
            user.DirectPermissions.Remove(grant);
        }
        foreach (var permission in permissions.Where(p => user.DirectPermissions.All(d => d.Permission != p)).ToList())
        {
            user.DirectPermissions.Add(new UserPermission { UserId = user.Id, Permission = permission });
        }
    }

    internal static bool IsAdministrator(Role role) => role.IsSystem && role.Name == Role.AdministratorName;

    /// <summary>Trimmed, non-empty, distinct (ordinal), in the given order.</summary>
    internal static string[] Normalize(IEnumerable<string?> values) =>
        values.Select(v => v?.Trim() ?? string.Empty).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private static string? NormalizeEmail(string? email) => string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    private static IResult NotFound() => ApiResults.NotFound("User not found");
}
