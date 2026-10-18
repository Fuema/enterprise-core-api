using System.Reflection;
using System.Text.RegularExpressions;
using AngularCrudApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Perm = AngularCrudApi.Auth.Permissions;

namespace AngularCrudApi.Data;

/// <summary>
/// Startup database tasks, each behind a flag in the "Database" configuration section:
///   ApplyBootstrapScripts  runs the embedded, idempotent Database/Scripts/*.sql in name order
///   SeedDevelopmentUsers   creates demo accounts when dbo.AppUsers is empty (Development only)
/// Both are enabled in appsettings.Development.json and disabled by default elsewhere.
/// </summary>
public static partial class DatabaseBootstrapper
{
    public static async Task InitializeAsync(WebApplication app)
    {
        var settings = app.Configuration.GetSection("Database");
        var applyScripts = settings.GetValue<bool>("ApplyBootstrapScripts");
        var seedUsers = settings.GetValue<bool>("SeedDevelopmentUsers") && app.Environment.IsDevelopment();
        if (!applyScripts && !seedUsers) return;

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseBootstrapper));

        if (applyScripts) await ApplyScriptsAsync(db, logger);
        if (seedUsers) await SeedDevelopmentUsersAsync(db, logger);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();

    private static async Task ApplyScriptsAsync(AppDbContext db, ILogger logger)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

        var connection = db.Database.GetDbConnection();
        if (connection is SqlConnection sql)
        {
            sql.InfoMessage += (_, e) => logger.LogWarning("SQL: {Message}", e.Message);
        }
        await db.Database.OpenConnectionAsync();
        try
        {
            foreach (var script in scripts)
            {
                await using var stream = assembly.GetManifestResourceStream(script)!;
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync();
                foreach (var batch in BatchSeparator().Split(text).Where(b => !string.IsNullOrWhiteSpace(b)))
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = batch;
                    await command.ExecuteNonQueryAsync();
                }
                logger.LogInformation("Applied database script {Script}", script);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Demo roles and accounts mirroring the React mock backend (password = username).
    /// Development only; never enable SeedDevelopmentUsers in production. Runs only on an empty AppUsers table,
    /// after the scripts (003 has already created the Administrator system role).
    /// </summary>
    private static async Task SeedDevelopmentUsersAsync(AppDbContext db, ILogger logger)
    {
        if (await db.Users.AnyAsync()) return;

        var administrator = await EnsureRoleAsync(db, Role.AdministratorName, "Full access to every feature, including future ones", isSystem: true, ["*"]);
        var hrManager = await EnsureRoleAsync(db, "HR Manager", "Manages employees and cities; reads countries and states", isSystem: false,
            [Perm.EmployeesRead, Perm.EmployeesWrite, Perm.CountriesRead, Perm.StatesRead, Perm.CitiesRead, Perm.CitiesWrite]);
        var auditor = await EnsureRoleAsync(db, "Auditor", "Read-only access to employees", isSystem: false, [Perm.EmployeesRead]);
        await EnsureRoleAsync(db, "User", "Default role of self-registered accounts", isSystem: false, [Perm.EmployeesRead]);

        var hasher = new PasswordHasher<AppUser>();
        (string Username, string DisplayName, Role Role)[] accounts =
        [
            ("admin", "Ada Admin", administrator),
            ("manager", "Max Manager", hrManager),
            ("viewer", "Vera Viewer", auditor),
        ];
        foreach (var (username, displayName, role) in accounts)
        {
            var user = new AppUser { Username = username, DisplayName = displayName, Email = $"{username}@example.test" };
            user.PasswordHash = hasher.HashPassword(user, username);
            user.UserRoles.Add(new UserRole { Role = role });
            db.Users.Add(user);
        }

        await db.SaveChangesAsync();
        logger.LogWarning("Seeded development users admin/admin, manager/manager, viewer/viewer. Change or remove them before sharing this database.");
    }

    private static async Task<Role> EnsureRoleAsync(AppDbContext db, string name, string description, bool isSystem, string[] permissions)
    {
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Name == name);
        if (role is not null) return role;
        role = new Role { Name = name, Description = description, IsSystem = isSystem };
        foreach (var permission in permissions) role.Permissions.Add(new RolePermission { Permission = permission });
        db.Roles.Add(role);
        return role;
    }
}
