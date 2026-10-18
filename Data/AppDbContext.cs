using AngularCrudApi.Models;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<State> States => Set<State>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("tblEmployeeMaster").HasKey(e => e.EmpId);
            // Created by Database/Scripts/001_auth_and_indexes.sql (skipped if duplicates exist).
            entity.HasIndex(e => e.EmailId).IsUnique().HasDatabaseName("UX_tblEmployeeMaster_EmailId");
        });
        modelBuilder.Entity<Country>().ToTable("CountryMaster");
        modelBuilder.Entity<State>().ToTable("StateMaster");
        modelBuilder.Entity<City>().ToTable("CityMaster");

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.ToTable("AppUsers");
            entity.HasIndex(u => u.Username).IsUnique().HasDatabaseName("UX_AppUsers_Username");
            entity.Property(u => u.Username).HasMaxLength(64);
            entity.Property(u => u.DisplayName).HasMaxLength(100);
            entity.Property(u => u.Email).HasMaxLength(256);
            entity.Property(u => u.PasswordHash).HasMaxLength(512);
            entity.Property(u => u.SecurityStamp).HasMaxLength(64);
            entity.HasMany(u => u.UserRoles).WithOne(ur => ur.User).HasForeignKey(ur => ur.UserId);
            entity.HasMany(u => u.DirectPermissions).WithOne().HasForeignKey(p => p.UserId);
        });

        // Tables created by Database/Scripts/003_rbac_security_audit.sql.
        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("Roles");
            entity.HasIndex(r => r.Name).IsUnique().HasDatabaseName("UX_Roles_Name");
            entity.Property(r => r.Name).HasMaxLength(64);
            entity.Property(r => r.Description).HasMaxLength(200);
            entity.HasMany(r => r.Permissions).WithOne().HasForeignKey(p => p.RoleId);
            entity.HasMany(r => r.UserRoles).WithOne(ur => ur.Role).HasForeignKey(ur => ur.RoleId);
        });
        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.ToTable("RolePermissions").HasKey(p => new { p.RoleId, p.Permission });
            entity.Property(p => p.Permission).HasMaxLength(100);
        });
        modelBuilder.Entity<UserRole>(entity => entity.ToTable("UserRoles").HasKey(ur => new { ur.UserId, ur.RoleId }));
        modelBuilder.Entity<UserPermission>(entity =>
        {
            entity.ToTable("UserPermissions").HasKey(p => new { p.UserId, p.Permission });
            entity.Property(p => p.Permission).HasMaxLength(100);
        });
        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("AuditLog");
            entity.Property(a => a.ActorName).HasMaxLength(100);
            entity.Property(a => a.Action).HasMaxLength(64);
            entity.Property(a => a.TargetType).HasMaxLength(32);
            entity.Property(a => a.TargetId).HasMaxLength(64);
            entity.Property(a => a.TargetName).HasMaxLength(100);
            entity.Property(a => a.IpAddress).HasMaxLength(64);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("UX_RefreshTokens_TokenHash");
            entity.Property(t => t.TokenHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            entity.Property(t => t.IpAddress).HasMaxLength(64);
            entity.Property(t => t.UserAgent).HasMaxLength(256);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
