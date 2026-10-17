using Microsoft.AspNetCore.Authorization;

namespace AngularCrudApi.Auth;

/// <summary>Claim types issued in the access token.</summary>
public static class AppClaims
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Email = "email";
    public const string Role = "role";
    public const string Permission = "permission";
}

/// <summary>
/// Permission scopes. They MUST match the React app (src/features/*/types, EMPLOYEE_PERMISSIONS).
/// Each scope is also an authorization policy name: .RequireAuthorization(Permissions.EmployeesRead).
/// </summary>
public static class Permissions
{
    public const string EmployeesRead = "employees:read";
    public const string EmployeesWrite = "employees:write";
    public const string EmployeesDelete = "employees:delete";

    public const string CountriesRead = "countries:read";
    public const string CountriesWrite = "countries:write";
    public const string CountriesDelete = "countries:delete";

    public const string StatesRead = "states:read";
    public const string StatesWrite = "states:write";
    public const string StatesDelete = "states:delete";

    public const string CitiesRead = "cities:read";
    public const string CitiesWrite = "cities:write";
    public const string CitiesDelete = "cities:delete";

    public static readonly string[] All =
    [
        EmployeesRead, EmployeesWrite, EmployeesDelete,
        CountriesRead, CountriesWrite, CountriesDelete,
        StatesRead, StatesWrite, StatesDelete,
        CitiesRead, CitiesWrite, CitiesDelete,
    ];

    /// <summary>
    /// Same rules as the frontend (src/lib/permissions.ts):
    /// exact scope, "resource:*" for every scope under a resource, "*" for super-users.
    /// </summary>
    public static bool Satisfies(IEnumerable<string> granted, string required)
    {
        var set = granted as ISet<string> ?? new HashSet<string>(granted, StringComparer.Ordinal);
        if (set.Contains("*") || set.Contains(required)) return true;

        var parts = required.Split(':');
        for (var i = parts.Length - 1; i > 0; i--)
        {
            if (set.Contains(string.Join(':', parts[..i]) + ":*")) return true;
        }
        return false;
    }
}

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var granted = context.User.FindAll(AppClaims.Permission).Select(claim => claim.Value);
        if (Permissions.Satisfies(granted, requirement.Permission))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

public static class PermissionPolicyExtensions
{
    /// <summary>Registers one authenticated policy per permission scope.</summary>
    public static IServiceCollection AddPermissionPolicies(this IServiceCollection services, IEnumerable<string> permissions)
    {
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        var builder = services.AddAuthorizationBuilder();
        foreach (var permission in permissions)
        {
            builder.AddPolicy(permission, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission)));
        }
        return services;
    }
}
