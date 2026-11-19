using System.Globalization;
using System.Text.RegularExpressions;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Data.Import;

public sealed record LegacyImportResult(
    int Users,
    int Branches,
    int Cuts,
    int Appointments,
    IReadOnlyDictionary<string, int> UsersByRole,
    IReadOnlyDictionary<string, int> AppointmentsByStatus);

public sealed partial class LegacyImporter(AppDbContext db, TimeProvider timeProvider)
{
    public const int DefaultCutDurationMinutes = 30;

    private static readonly string[] SequenceResets = new[] { "Users", "Branches", "Cuts", "Appointments" }
        .Select(table =>
            $"SELECT setval(pg_get_serial_sequence('\"{table}\"', 'id'), COALESCE((SELECT MAX(id) FROM \"{table}\"), 1))")
        .ToArray();

    public async Task<LegacyImportResult> ImportAsync(IReadOnlyDictionary<string, DumpTable> dump, CancellationToken cancellationToken)
    {
        await EnsureTargetIsEmptyAsync(cancellationToken);

        var branches = Table(dump, "Branches").Rows.Select(MapBranch).ToList();
        var cuts = Table(dump, "cuts").Rows.Select(MapCut).ToList();
        var users = Table(dump, "Users").Rows.Select(MapUser).ToList();
        EnsureUnique(users.Select(u => u.Email), "Hay emails repetidos en el respaldo.");
        EnsureReferences(users, branches);

        var now = timeProvider.GetUtcNow();
        var cutsById = cuts.ToDictionary(c => c.Id);
        var usersById = users.ToDictionary(u => u.Id);
        var appointments = Table(dump, "Appointments").Rows
            .Select(row => MapAppointment(row, usersById, cutsById, now))
            .ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Branches.AddRange(branches);
        db.Cuts.AddRange(cuts);
        db.Users.AddRange(users);
        await db.SaveChangesAsync(cancellationToken);
        db.Appointments.AddRange(appointments);
        await db.SaveChangesAsync(cancellationToken);

        if (db.Database.IsRelational())
        {
            foreach (var sql in SequenceResets)
            {
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new LegacyImportResult(
            users.Count,
            branches.Count,
            cuts.Count,
            appointments.Count,
            users.GroupBy(u => u.Role.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count()),
            appointments.GroupBy(a => a.Status.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count()));
    }

    private async Task EnsureTargetIsEmptyAsync(CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(cancellationToken) || await db.Branches.AnyAsync(cancellationToken) ||
            await db.Cuts.AnyAsync(cancellationToken) || await db.Appointments.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException("La base ya tiene usuarios, sucursales, servicios o turnos: la importación solo se hace sobre una base vacía.");
        }
    }

    private static DumpTable Table(IReadOnlyDictionary<string, DumpTable> dump, string name) =>
        dump.TryGetValue(name, out var table)
            ? table
            : throw new InvalidOperationException($"El respaldo no tiene la tabla '{name}'.");

    private static Branch MapBranch(DumpRow row) => new()
    {
        Id = ParseInt(row.Required("id")),
        Name = row.Required("name").Trim(),
        Address = row.Required("address").Trim(),
        Phone = Optional(row["phone"]),
        ImageUrl = Optional(row["imageUrl"]),
        IsActive = true,
        CreatedAt = ParseTimestamp(row.Required("createdAt")),
        UpdatedAt = ParseTimestamp(row.Required("updatedAt")),
    };

    private static Cut MapCut(DumpRow row) => new()
    {
        Id = ParseInt(row.Required("id")),
        Name = row.Required("name").Trim(),
        Price = decimal.Parse(row.Required("price"), NumberStyles.Number, CultureInfo.InvariantCulture),
        DurationMinutes = DefaultCutDurationMinutes,
        IsActive = true,
        CreatedAt = ParseTimestamp(row.Required("createdAt")),
        UpdatedAt = ParseTimestamp(row.Required("updatedAt")),
    };

    private static User MapUser(DumpRow row)
    {
        var role = Enum.Parse<UserRole>(row.Required("role"), ignoreCase: true);
        var passwordHash = row.Required("password");
        if (!BcryptHash().IsMatch(passwordHash))
        {
            throw new InvalidOperationException($"El usuario {row["id"]} no tiene un hash bcrypt válido.");
        }

        return new User
        {
            Id = ParseInt(row.Required("id")),
            Name = row.Required("name").Trim(),
            Email = User.NormalizeEmail(row.Required("email")),
            PasswordHash = passwordHash,
            Phone = role == UserRole.Barber ? null : Optional(row["phone"]),
            ImageUrl = Optional(row["imageUrl"]),
            BranchId = role == UserRole.Barber ? ParseInt(row.Required("branchId")) : null,
            StartDate = Optional(row["startDate"]) is { } start ? DateOnly.Parse(start[..10], CultureInfo.InvariantCulture) : null,
            Role = role,
            IsActive = true,
            EmailVerified = true,
            CreatedAt = ParseTimestamp(row.Required("createdAt")),
            UpdatedAt = ParseTimestamp(row.Required("updatedAt")),
        };
    }

    private static Appointment MapAppointment(
        DumpRow row,
        IReadOnlyDictionary<int, User> users,
        IReadOnlyDictionary<int, Cut> cuts,
        DateTimeOffset now)
    {
        var id = ParseInt(row.Required("id"));
        var start = ParseTimestamp(row.Required("appointmentDate"));
        var updatedAt = ParseTimestamp(row.Required("updatedAt"));

        if (!cuts.TryGetValue(ParseInt(row.Required("cutId")), out var cut))
        {
            throw new InvalidOperationException($"El turno {id} tiene un servicio inexistente.");
        }

        if (!users.TryGetValue(ParseInt(row.Required("barberId")), out var barber) || barber.Role != UserRole.Barber)
        {
            throw new InvalidOperationException($"El turno {id} no tiene un barbero válido.");
        }

        User? client = null;
        if (Optional(row["clientId"]) is { } clientValue && !users.TryGetValue(ParseInt(clientValue), out client))
        {
            throw new InvalidOperationException($"El turno {id} tiene un cliente inexistente.");
        }

        var appointment = new Appointment
        {
            Id = id,
            AppointmentDate = start,
            EndDate = start.AddMinutes(cut.DurationMinutes),
            ClientId = client?.Id,
            ClientName = client is null ? "Cliente eliminado" : null,
            BarberId = barber.Id,
            CutId = cut.Id,
            BranchId = barber.BranchId!.Value,
            Price = cut.Price,
            CreatedByRole = UserRole.Client,
            CreatedById = client?.Id,
            CreatedAt = ParseTimestamp(row.Required("createdAt")),
            UpdatedAt = updatedAt,
        };

        switch (row.Required("status").ToLowerInvariant())
        {
            case "completed":
            case "pending" or "confirmed" when start < now:
                appointment.Status = AppointmentStatus.Completed;
                appointment.PaymentStatus = PaymentStatus.Paid;
                appointment.PaymentMethod = PaymentMethod.Cash;
                appointment.PaidAt = start;
                break;
            case "pending" or "confirmed":
                appointment.Status = AppointmentStatus.Pending;
                appointment.PaymentStatus = PaymentStatus.Unpaid;
                appointment.PaymentMethod = PaymentMethod.Cash;
                break;
            case "cancelled":
                appointment.Status = AppointmentStatus.Cancelled;
                appointment.PaymentStatus = PaymentStatus.Unpaid;
                appointment.CancelledAt = updatedAt;
                break;
            default:
                throw new InvalidOperationException($"El turno {id} tiene un estado desconocido.");
        }

        return appointment;
    }

    private static void EnsureUnique(IEnumerable<string> values, string message)
    {
        var list = values.ToList();
        if (list.Distinct(StringComparer.Ordinal).Count() != list.Count)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void EnsureReferences(IEnumerable<User> users, IEnumerable<Branch> branches)
    {
        var branchIds = branches.Select(b => b.Id).ToHashSet();
        var invalid = users.FirstOrDefault(u => u.BranchId is { } branchId && !branchIds.Contains(branchId));
        if (invalid is not null)
        {
            throw new InvalidOperationException($"El usuario {invalid.Id} apunta a una sucursal inexistente.");
        }
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParseInt(string value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        var normalized = ShortOffset().Replace(value.Trim(), "$1:00");
        return DateTimeOffset.Parse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
    }

    [GeneratedRegex(@"([+-]\d{2})$")]
    private static partial Regex ShortOffset();

    [GeneratedRegex(@"^\$2[aby]\$\d{2}\$[./A-Za-z0-9]{53}$")]
    private static partial Regex BcryptHash();
}
