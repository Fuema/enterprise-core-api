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
    /// Demo accounts mirroring the React mock backend (password = username).
    /// Development only; never enable SeedDevelopmentUsers in production.
    /// </summary>
    /// <summary>Demo "manager": edits employees and cities, can only look at countries and states.</summary>
    private const string ManagerPermissions =
        $"{Perm.EmployeesRead},{Perm.EmployeesWrite},{Perm.CountriesRead},{Perm.StatesRead},{Perm.CitiesRead},{Perm.CitiesWrite}";

    private static async Task SeedDevelopmentUsersAsync(AppDbContext db, ILogger logger)
    {
        if (await db.Users.AnyAsync()) return;

        var hasher = new PasswordHasher<AppUser>();
        AppUser[] users =
        [
            new() { Username = "admin", DisplayName = "Ada Admin", Email = "admin@example.test", Roles = "Administrator", Permissions = "*" },
            new() { Username = "manager", DisplayName = "Max Manager", Email = "manager@example.test", Roles = "HR Manager",
                    Permissions = ManagerPermissions },
            new() { Username = "viewer", DisplayName = "Vera Viewer", Email = "viewer@example.test", Roles = "Auditor",
                    Permissions = Perm.EmployeesRead },
        ];
        foreach (var user in users)
        {
            user.PasswordHash = hasher.HashPassword(user, user.Username);
        }

        db.Users.AddRange(users);
        await db.SaveChangesAsync();
        logger.LogWarning("Seeded development users admin/admin, manager/manager, viewer/viewer. Change or remove them before sharing this database.");
    }
}
