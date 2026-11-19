using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Email;

public sealed class AccountEmailService(
    AppDbContext db,
    IEmailQueue queue,
    IOptions<EmailSettings> settings,
    TimeProvider time)
{
    public const string VerifyEmailPath = "verify-email";
    public const string ResetPasswordPath = "reset-password";

    public Task SendVerificationAsync(User user, string token, CancellationToken cancellationToken) =>
        QueueAsync(user, NotificationType.EmailVerification,
            AccountEmailTemplates.Verification(user, Link(VerifyEmailPath, token)), cancellationToken);

    public Task SendPasswordResetAsync(User user, string token, CancellationToken cancellationToken) =>
        QueueAsync(user, NotificationType.PasswordReset,
            AccountEmailTemplates.PasswordReset(user, Link(ResetPasswordPath, token)), cancellationToken);

    private string Link(string path, string token) =>
        $"{settings.Value.FrontendBaseUrl.TrimEnd('/')}/{path}?token={Uri.EscapeDataString(token)}";

    private async Task QueueAsync(User user, NotificationType type, EmailMessage message, CancellationToken cancellationToken)
    {
        var notification = new Notification
        {
            UserId = user.Id,
            Type = type,
            Channel = NotificationChannel.Email,
            Status = NotificationStatus.Pending,
            ScheduledAt = time.GetUtcNow(),
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken);

        queue.Enqueue(new QueuedEmail(notification.Id, message));
    }
}
