using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Name).IsRequired().HasMaxLength(100);
            entity.Property(u => u.Email).IsRequired().HasMaxLength(254);
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Property(u => u.PasswordHash).HasColumnName("password").IsRequired().HasMaxLength(100);
            entity.Property(u => u.Phone).HasMaxLength(30);
            entity.Property(u => u.ImageUrl).HasMaxLength(500);
            entity.Property(u => u.Role)
                .HasConversion(role => role.ToString().ToLowerInvariant(), value => Enum.Parse<UserRole>(value, true))
                .HasMaxLength(20);
            entity.Property(u => u.IsActive).HasDefaultValue(true);
            entity.Property(u => u.EmailVerified).HasDefaultValue(false);
        });

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.GetColumnName() == property.Name)
                {
                    property.SetColumnName(char.ToLowerInvariant(property.Name[0]) + property.Name[1..]);
                }
            }
        }
    }
}
