using CraneoBarbero.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Data.Configurations;

public static class SupportConfiguration
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Review>(entity =>
        {
            entity.ToTable("Reviews", table =>
            {
                table.HasCheckConstraint("CK_Reviews_ratingBarber", "\"ratingBarber\" BETWEEN 1 AND 5");
                table.HasCheckConstraint("CK_Reviews_ratingCut", "\"ratingCut\" BETWEEN 1 AND 5");
            });
            entity.HasIndex(r => r.AppointmentId).IsUnique();
            entity.Property(r => r.Comment).HasColumnType("text");
            entity.HasOne(r => r.Appointment).WithMany().HasForeignKey(r => r.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.ToTable("Notifications");
            entity.HasOne<User>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Appointment>().WithMany().HasForeignKey(n => n.AppointmentId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(n => new { n.AppointmentId, n.Type });
            entity.HasIndex(n => new { n.Status, n.ScheduledAt });
        });

        modelBuilder.Entity<AuthToken>(entity =>
        {
            entity.ToTable("AuthTokens");
            entity.Property(t => t.TokenHash).IsRequired().HasMaxLength(128);
            entity.Property(t => t.UsedAt).IsConcurrencyToken();
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => new { t.UserId, t.Type });
            entity.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
