using CraneoBarbero.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Data.Configurations;

public static class UsersAndCatalogConfiguration
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users", table =>
            {
                table.HasCheckConstraint("CK_Users_email_normalized", "\"email\" = lower(btrim(\"email\"))");
                table.HasCheckConstraint("CK_Users_branch_by_role",
                    "(\"role\" = 'barber' AND \"branchId\" IS NOT NULL) OR (\"role\" <> 'barber' AND \"branchId\" IS NULL)");
                table.HasCheckConstraint("CK_Users_barber_without_phone", "\"role\" <> 'barber' OR \"phone\" IS NULL");
            });
            entity.Property(u => u.Name).IsRequired().HasMaxLength(100);
            entity.Property(u => u.Email).IsRequired().HasMaxLength(254);
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Property(u => u.PasswordHash).HasColumnName("password").IsRequired().HasMaxLength(100);
            entity.Property(u => u.Phone).HasMaxLength(30);
            entity.Property(u => u.ImageUrl).HasMaxLength(500);
            entity.Property(u => u.IsActive).HasDefaultValue(true).HasSentinel(true);
            entity.Property(u => u.EmailVerified).HasDefaultValue(false);
            entity.HasOne(u => u.Branch).WithMany().HasForeignKey(u => u.BranchId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(u => new { u.Role, u.IsActive });
        });

        modelBuilder.Entity<Branch>(entity =>
        {
            entity.ToTable("Branches");
            entity.Property(b => b.Name).IsRequired().HasMaxLength(100);
            entity.Property(b => b.Address).IsRequired().HasMaxLength(200);
            entity.Property(b => b.Phone).HasMaxLength(30);
            entity.Property(b => b.ImageUrl).HasMaxLength(500);
            entity.Property(b => b.IsActive).HasDefaultValue(true).HasSentinel(true);
        });

        modelBuilder.Entity<Cut>(entity =>
        {
            entity.ToTable("Cuts", table =>
            {
                table.HasCheckConstraint("CK_Cuts_price", "\"price\" >= 0");
                table.HasCheckConstraint("CK_Cuts_durationMinutes", "\"durationMinutes\" > 0");
            });
            entity.Property(c => c.Name).IsRequired().HasMaxLength(100);
            entity.Property(c => c.Price).HasPrecision(10, 2);
            entity.Property(c => c.Description).HasColumnType("text");
            entity.Property(c => c.IsActive).HasDefaultValue(true).HasSentinel(true);
        });

        modelBuilder.Entity<Holiday>(entity =>
        {
            entity.ToTable("Holidays", table =>
                table.HasCheckConstraint("CK_Holidays_source", "\"source\" IN ('api', 'manual')"));
            entity.HasIndex(h => h.Date).IsUnique();
            entity.Property(h => h.Name).IsRequired().HasMaxLength(150);
            entity.Property(h => h.Source).IsRequired().HasMaxLength(10);
            entity.Property(h => h.IsActive).HasDefaultValue(true).HasSentinel(true);
        });

        modelBuilder.Entity<AvailableSlot>(entity =>
        {
            entity.ToTable("AvailableSlots");
            entity.HasIndex(s => new { s.BarberId, s.StartAt }, "IX_AvailableSlots_barberId_startAt").IsUnique();
            entity.HasIndex(s => s.StartAt);
            entity.HasIndex(s => new { s.BarberId, s.StartAt }, "IX_AvailableSlots_open")
                .HasFilter("\"deletedAt\" IS NULL");
            entity.Property(s => s.DeleteReason).HasMaxLength(300);
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.BarberId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.CreatedById).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.DeletedById).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
