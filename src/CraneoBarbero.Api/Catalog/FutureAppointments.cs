using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Catalog;

public sealed class FutureAppointments(AppDbContext db, TimeProvider time)
{
    public Task<int> CountForBranchAsync(int branchId, CancellationToken cancellationToken) =>
        Upcoming().CountAsync(a => a.BranchId == branchId, cancellationToken);

    public Task<int> CountForBarberAsync(int barberId, CancellationToken cancellationToken) =>
        Upcoming().CountAsync(a => a.BarberId == barberId, cancellationToken);

    public Task<int> CountForCutAsync(int cutId, CancellationToken cancellationToken) =>
        Upcoming().CountAsync(a => a.CutId == cutId, cancellationToken);

    private IQueryable<Appointment> Upcoming()
    {
        var now = time.GetUtcNow();
        return db.Appointments.Where(a =>
            (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.AwaitingPayment) &&
            a.EndDate > now);
    }

    public static string Describe(int count) => count == 1 ? "1 turno futuro" : $"{count} turnos futuros";
}
