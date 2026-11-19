using CraneoBarbero.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Data.Configurations;

public static class AppointmentsConfiguration
{
    public const string BarberOverlapConstraint = "EX_Appointments_barber_overlap";
    public const string ClientOverlapConstraint = "EX_Appointments_client_overlap";

    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.ToTable("Appointments", table =>
            {
                table.HasCheckConstraint("CK_Appointments_endDate", "\"endDate\" > \"appointmentDate\"");
                table.HasCheckConstraint("CK_Appointments_client", "\"clientId\" IS NOT NULL OR \"clientName\" IS NOT NULL");
                table.HasCheckConstraint("CK_Appointments_price", "\"price\" >= 0");
            });
            entity.Property(a => a.ClientName).HasMaxLength(100);
            entity.Property(a => a.Price).HasPrecision(10, 2);
            entity.Property(a => a.CancellationReason).HasMaxLength(300);

            entity.HasOne(a => a.Client).WithMany().HasForeignKey(a => a.ClientId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(a => a.Barber).WithMany().HasForeignKey(a => a.BarberId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(a => a.Cut).WithMany().HasForeignKey(a => a.CutId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(a => a.Branch).WithMany().HasForeignKey(a => a.BranchId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(a => a.CreatedById).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<User>().WithMany().HasForeignKey(a => a.CancelledById).OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(a => new { a.BarberId, a.AppointmentDate });
            entity.HasIndex(a => new { a.ClientId, a.AppointmentDate });
            entity.HasIndex(a => new { a.Status, a.PaymentExpiresAt });
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("Payments", table =>
            {
                table.HasCheckConstraint("CK_Payments_single_target",
                    "(\"orderId\" IS NOT NULL AND \"appointmentId\" IS NULL) OR (\"orderId\" IS NULL AND \"appointmentId\" IS NOT NULL)");
                table.HasCheckConstraint("CK_Payments_amount", "\"amount\" > 0");
            });
            entity.Property(p => p.Amount).HasPrecision(10, 2);
            entity.Property(p => p.ExternalId).HasMaxLength(100);
            entity.HasIndex(p => p.ExternalId).IsUnique();
            entity.Property(p => p.PreferenceId).HasMaxLength(100);
            entity.HasOne(p => p.Order).WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(p => p.Appointment).WithMany().HasForeignKey(p => p.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
