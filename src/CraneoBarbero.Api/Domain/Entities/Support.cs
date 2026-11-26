using CraneoBarbero.Api.Domain.Enums;

namespace CraneoBarbero.Api.Domain.Entities;

public class Review : IHasTimestamps
{
    public int Id { get; set; }
    public int AppointmentId { get; set; }
    public short RatingBarber { get; set; }
    public short RatingCut { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Appointment? Appointment { get; set; }
}

public class Notification : IHasCreatedAt
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? AppointmentId { get; set; }
    public NotificationType Type { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.Email;
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public DateTimeOffset ScheduledAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class AuthToken : IHasCreatedAt
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AuthTokenType Type { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class AvailableSlot : IHasCreatedAt
{
    public int Id { get; set; }
    public int BarberId { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public SlotOrigin Origin { get; set; } = SlotOrigin.Auto;
    public DateTimeOffset CreatedAt { get; set; }
    public int? CreatedById { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public int? DeletedById { get; set; }
    public string? DeleteReason { get; set; }
}

public class Holiday : IHasTimestamps
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = string.Empty;
    public HolidayType Type { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class MonthlyReport : IHasCreatedAt
{
    public int Id { get; set; }
    public string Month { get; set; } = string.Empty;
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public bool AiGenerated { get; set; }
    public int EmailsSent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
