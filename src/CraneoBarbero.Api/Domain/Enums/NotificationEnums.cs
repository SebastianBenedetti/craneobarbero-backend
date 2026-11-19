namespace CraneoBarbero.Api.Domain.Enums;

public enum NotificationType
{
    Confirmation,
    Reminder,
    Cancellation,
}

public enum NotificationChannel
{
    Email,
}

public enum NotificationStatus
{
    Pending,
    Sent,
    Failed,
}
