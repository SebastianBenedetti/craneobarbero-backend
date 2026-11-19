using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Schedule;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CraneoBarbero.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Tests.Schedule;

public sealed class FakeHolidaySource : IHolidaySource
{
    public Dictionary<int, List<ExternalHoliday>> Years { get; } = [];
    public HashSet<int> Failing { get; } = [];

    public Task<IReadOnlyList<ExternalHoliday>> GetNationalHolidaysAsync(int year, CancellationToken cancellationToken)
    {
        if (Failing.Contains(year))
        {
            throw new HttpRequestException("sin conexión");
        }

        IReadOnlyList<ExternalHoliday> result = Years.TryGetValue(year, out var list) ? list.ToList() : [];
        return Task.FromResult(result);
    }
}

public sealed class AdjustableTime(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void SetUtcNow(DateTimeOffset value) => _now = value;
}

public class ScheduleApiFactory : ApiFactory
{
    public static readonly DateTimeOffset Now = new(2026, 11, 23, 11, 0, 0, TimeSpan.Zero);

    public AdjustableTime Time { get; } = new(Now);
    public FakeHolidaySource Holidays { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<BusinessClock>();
            services.AddSingleton(provider => new BusinessClock(Time, provider.GetRequiredService<IOptions<BusinessSettings>>()));
            services.RemoveAll<IHolidaySource>();
            services.AddSingleton<IHolidaySource>(Holidays);
        });
    }

    public static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(-3)).ToUniversalTime();

    public async Task<(Branch Branch, User Barber)> AddBarberAsync(bool isActive = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var branch = new Branch { Name = "Sucursal Agenda", Address = "Calle 1" };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        var barber = new User
        {
            Name = "Barbero Agenda",
            Email = $"agenda-{Guid.NewGuid():N}@example.com",
            PasswordHash = "$2b$12$abcdefghijklmnopqrstuuS3mZ3zvXnmQbgLSzMNFyK3x0BxhwJqq",
            Role = UserRole.Barber,
            BranchId = branch.Id,
            IsActive = isActive,
            EmailVerified = true,
        };
        db.Users.Add(barber);
        await db.SaveChangesAsync();
        return (branch, barber);
    }

    public async Task<User> AddBarberInBranchAsync(int branchId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var barber = new User
        {
            Name = "Otro Barbero",
            Email = $"otro-{Guid.NewGuid():N}@example.com",
            PasswordHash = "$2b$12$abcdefghijklmnopqrstuuS3mZ3zvXnmQbgLSzMNFyK3x0BxhwJqq",
            Role = UserRole.Barber,
            BranchId = branchId,
            EmailVerified = true,
        };
        db.Users.Add(barber);
        await db.SaveChangesAsync();
        return barber;
    }

    public async Task<Cut> AddCutAsync(int minutes, bool isActive = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cut = new Cut { Name = $"Servicio {minutes}", Price = 1000m, DurationMinutes = minutes, IsActive = isActive };
        db.Cuts.Add(cut);
        await db.SaveChangesAsync();
        return cut;
    }

    public async Task<SlotGenerationResult> GenerateAsync(int? barberId = null, DateOnly? from = null, DateOnly? to = null)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SlotGenerator>().GenerateAsync(barberId, from, to);
    }

    public async Task<HolidaySyncResult> SyncHolidaysAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<HolidayService>().SyncAsync();
    }

    public Task<List<AvailableSlot>> SlotsAsync(int barberId, DateOnly date) => WithDbAsync(db =>
    {
        var from = Local(date.Year, date.Month, date.Day, 0);
        var to = from.AddDays(1);
        return db.AvailableSlots.AsNoTracking()
            .Where(s => s.BarberId == barberId && s.StartAt >= from && s.StartAt < to)
            .OrderBy(s => s.StartAt)
            .ToListAsync();
    });

    public Task AddAppointmentAsync(User barber, Cut cut, DateTimeOffset start, AppointmentStatus status = AppointmentStatus.Pending, int? clientId = null) =>
        WithDbAsync(async db =>
        {
            db.Appointments.Add(new Appointment
            {
                AppointmentDate = start,
                EndDate = start.AddMinutes(cut.DurationMinutes),
                Status = status,
                ClientId = clientId,
                ClientName = clientId is null ? "Cliente" : null,
                BarberId = barber.Id,
                CutId = cut.Id,
                BranchId = barber.BranchId!.Value,
                Price = cut.Price,
                CreatedByRole = UserRole.Admin,
            });
            return await db.SaveChangesAsync();
        });

    public Task SetSlotsAsync(int barberId, IEnumerable<DateTimeOffset> starts, SlotOrigin origin = SlotOrigin.Manual) =>
        WithDbAsync(async db =>
        {
            db.AvailableSlots.AddRange(starts.Select(start => new AvailableSlot { BarberId = barberId, StartAt = start, Origin = origin }));
            return await db.SaveChangesAsync();
        });
}
