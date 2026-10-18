using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Infrastructure;
using AngularCrudApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Endpoint;

/// <summary>
/// Roles (named permission sets):
///   GET    /api/roles?page=&amp;pageSize=&amp;search=&amp;sort=      roles:read
///   GET    /api/roles/{id}  (with members)                       roles:read
///   POST   /api/roles { name, description, permissions } -> 201  roles:write
///   PUT    /api/roles/{id} { name, description, permissions }    roles:write
///   DELETE /api/roles/{id} -> 204; 409 while assigned            roles:delete
/// The Administrator system role is locked: only its description can change, it always has "*",
/// and it cannot be deleted. Changing a role's permissions rotates every member's security stamp, so
/// the change applies on their next request.
/// </summary>
public static class RoleEndpoints
{
    private const int MaxMembersShown = 200;

    private static readonly SortMap<Role> Sorts = new SortMap<Role>()
        .Add("id", r => r.Id)
        .Add("name", r => r.Name)
        .Add("userCount", r => r.UserRoles.Count)
        .Add("permissionCount", r => r.Permissions.Count)
        .Add("createdAtUtc", r => r.CreatedAtUtc);

    public static void MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/roles").WithTags("Roles").RequireAuthorization(Permissions.RolesRead);

        group.MapGet("", GetPage);
        group.MapGet("{id:int}", GetById).WithName("GetRoleById");
        group.MapPost("", Create).RequireAuthorization(Permissions.RolesWrite).WithValidation<RoleRequest>();
        group.MapPut("{id:int}", Update).RequireAuthorization(Permissions.RolesWrite).WithValidation<RoleRequest>();
        group.MapDelete("{id:int}", Delete).RequireAuthorization(Permissions.RolesDelete);
    }

    private static async Task<IResult> GetPage([AsParameters] PageRequest paging, AppDbContext db, CancellationToken ct)
    {
        var query = db.Roles.AsNoTracking();
        foreach (var term in paging.SearchTerms)
        {
            query = query.Where(r => r.Name.Contains(term) || (r.Description != null && r.Description.Contains(term)));
        }
        var page = await Sorts.Apply(query, paging.Sort, "name.asc").ThenBy(r => r.Id)
            .Select(r => new RoleListItem
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                IsSystem = r.IsSystem,
                UserCount = r.UserRoles.Count,
                PermissionCount = r.Permissions.Count,
                Permissions = r.Permissions.Select(p => p.Permission).OrderBy(p => p).ToList(),
                CreatedAtUtc = r.CreatedAtUtc,
                UpdatedAtUtc = r.UpdatedAtUtc,
            })
            .ToPagedAsync(paging, ct);
        return Results.Ok(page);
    }

    private static async Task<IResult> GetById(int id, AppDbContext db, CancellationToken ct)
    {
        var detail = await LoadDetailAsync(db, id, ct);
        return detail is null ? NotFound() : Results.Ok(detail);
    }

    private static async Task<IResult> Create(
        [FromBody] RoleRequest request, AppDbContext db, AuditService audit, CancellationToken ct)
    {
        var name = request.Name.Trim();
        var permissions = UserEndpoints.Normalize(request.Permissions);
        var errors = new Dictionary<string, string[]>();
        if (await db.Roles.AnyAsync(r => r.Name == name, ct)) errors["name"] = ["A role with this name already exists"];
        UserEndpoints.ValidatePermissions(permissions, "permissions", errors);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var role = new Role { Name = name, Description = NormalizeDescription(request.Description), CreatedAtUtc = DateTime.UtcNow };
        foreach (var permission in permissions) role.Permissions.Add(new RolePermission { Permission = permission });
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);

        audit.Record(AuditActions.RoleCreated, "role", role.Id.ToString(), role.Name, new { permissions });
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetRoleById", new { id = role.Id }, await LoadDetailAsync(db, role.Id, ct));
    }

    private static async Task<IResult> Update(
        int id, [FromBody] RoleRequest request, HttpContext http, AppDbContext db, AuditService audit,
        SessionValidator validator, CancellationToken ct)
    {
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return NotFound();

        var name = request.Name.Trim();
        var permissions = UserEndpoints.Normalize(request.Permissions);
        var errors = new Dictionary<string, string[]>();
        var oldPermissions = role.Permissions.Select(p => p.Permission).ToHashSet();

        if (role.IsSystem)
        {
            if (name != role.Name) errors["name"] = ["System roles cannot be renamed"];
            if (!oldPermissions.SetEquals(permissions)) errors["permissions"] = ["The Administrator role always has every permission"];
        }
        else if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != id, ct))
        {
            errors["name"] = ["A role with this name already exists"];
        }
        UserEndpoints.ValidatePermissions(permissions, "permissions", errors);

        // Lock-out protection: the editor must keep users:write through this role or their other access.
        var memberIds = await db.UserRoles.Where(ur => ur.RoleId == id).Select(ur => ur.UserId).ToListAsync(ct);
        var currentUserId = AuthEndpoints.CurrentUserId(http);
        if (errors.Count == 0 && currentUserId is { } me && memberIds.Contains(me) && !oldPermissions.SetEquals(permissions))
        {
            var otherRolePermissions = await db.UserRoles
                .Where(ur => ur.UserId == me && ur.RoleId != id)
                .SelectMany(ur => ur.Role.Permissions.Select(p => p.Permission))
                .ToListAsync(ct);
            var direct = await db.UserPermissions.Where(p => p.UserId == me).Select(p => p.Permission).ToListAsync(ct);
            if (!Permissions.Satisfies(otherRolePermissions.Concat(direct).Concat(permissions), Permissions.UsersWrite))
            {
                errors["permissions"] = ["This change would remove your own users:write permission"];
            }
        }
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var changes = new Dictionary<string, object?>();
        if (role.Name != name) changes["name"] = new[] { role.Name, name };
        var description = NormalizeDescription(request.Description);
        if (role.Description != description) changes["description"] = new[] { role.Description, description };
        var permissionsChanged = !oldPermissions.SetEquals(permissions);
        if (permissionsChanged)
        {
            changes["permissions"] = new { added = permissions.Except(oldPermissions), removed = oldPermissions.Except(permissions) };
        }

        role.Name = name;
        role.Description = description;
        role.UpdatedAtUtc = DateTime.UtcNow;
        foreach (var grant in role.Permissions.Where(p => !permissions.Contains(p.Permission)).ToList()) role.Permissions.Remove(grant);
        foreach (var permission in permissions.Where(p => role.Permissions.All(x => x.Permission != p)).ToList())
        {
            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = permission });
        }
        if (changes.Count > 0) audit.Record(AuditActions.RoleUpdated, "role", role.Id.ToString(), role.Name, new { changes, members = memberIds.Count });
        await db.SaveChangesAsync(ct);

        // Role names travel in tokens too: any visible change re-issues the members' tokens on their next request.
        if (permissionsChanged || changes.ContainsKey("name"))
        {
            await RotateStampsAsync(db, memberIds, ct);
            foreach (var userId in memberIds) validator.InvalidateUser(userId);
        }

        return Results.Ok(await LoadDetailAsync(db, id, ct));
    }

    private static async Task<IResult> Delete(int id, AppDbContext db, AuditService audit, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return NotFound();
        if (role.IsSystem) return ApiResults.Conflict("System role", $"{role.Name} is a system role and cannot be deleted.");

        var members = await db.UserRoles.CountAsync(ur => ur.RoleId == id, ct);
        if (members > 0)
        {
            return ApiResults.Conflict("Role is in use",
                $"{role.Name} is assigned to {ApiResults.Plural(members, "user", "users")}. Remove it from them first.");
        }

        db.Roles.Remove(role);
        audit.Record(AuditActions.RoleDeleted, "role", role.Id.ToString(), role.Name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // -------------------------------------------------------------------------

    private static async Task<RoleDetailResponse?> LoadDetailAsync(AppDbContext db, int id, CancellationToken ct)
    {
        var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return null;
        var userCount = await db.UserRoles.CountAsync(ur => ur.RoleId == id, ct);
        var members = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.RoleId == id)
            .OrderBy(ur => ur.User.Username)
            .Take(MaxMembersShown)
            .Select(ur => new RoleMember(ur.User.Id, ur.User.Username, ur.User.DisplayName, ur.User.IsActive))
            .ToArrayAsync(ct);
        return new RoleDetailResponse(
            role.Id, role.Name, role.Description, role.IsSystem,
            AccessService.Effective(role.Permissions.Select(p => p.Permission)),
            userCount, members, role.CreatedAtUtc, role.UpdatedAtUtc);
    }

    /// <summary>New security stamp for each member (their next request re-issues a token with the new access).</summary>
    private static async Task RotateStampsAsync(AppDbContext db, List<int> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return;
        var users = await db.Users.Where(u => userIds.Contains(u.Id)).ToListAsync(ct);
        foreach (var user in users) user.RotateSecurityStamp();
        await db.SaveChangesAsync(ct);
    }

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static IResult NotFound() => ApiResults.NotFound("Role not found");
}
