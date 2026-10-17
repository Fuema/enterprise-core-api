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
            entity.Property(u => u.Roles).HasMaxLength(400);
            entity.Property(u => u.Permissions).HasMaxLength(1000);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("UX_RefreshTokens_TokenHash");
            entity.Property(t => t.TokenHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            entity.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
