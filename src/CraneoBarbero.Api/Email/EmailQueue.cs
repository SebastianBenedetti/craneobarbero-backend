using System.Threading.Channels;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Email;

public sealed record QueuedEmail(int NotificationId, EmailMessage Message);

public interface IEmailQueue
{
    void Enqueue(QueuedEmail email);
}

public sealed class EmailQueue : IEmailQueue
{
    private readonly Channel<QueuedEmail> _channel =
        Channel.CreateUnbounded<QueuedEmail>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(QueuedEmail email)
    {
        if (!_channel.Writer.TryWrite(email))
        {
            throw new InvalidOperationException("La cola de mails está cerrada.");
        }
    }

    public IAsyncEnumerable<QueuedEmail> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class EmailDispatcher(
    EmailQueue queue,
    IServiceScopeFactory scopes,
    IOptions<EmailSettings> settings,
    TimeProvider time,
    ILogger<EmailDispatcher> logger) : BackgroundService
{
    private const int MaxAttempts = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var email in queue.ReadAllAsync(stoppingToken))
            {
                await DispatchAsync(email, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task DispatchAsync(QueuedEmail email, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        var sent = false;
        for (var attempt = 1; attempt <= MaxAttempts && !sent; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.Value.RetryDelaySeconds), time, cancellationToken);
            }

            sent = await TrySendAsync(sender, email, cancellationToken);
        }

        try
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notification = await db.Notifications.FindAsync([email.NotificationId], cancellationToken);
            if (notification is null)
            {
                return;
            }

            notification.Status = sent ? NotificationStatus.Sent : NotificationStatus.Failed;
            notification.SentAt = sent ? time.GetUtcNow() : null;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "No se pudo registrar el estado de la notificación {NotificationId}.",
                email.NotificationId);
        }
    }

    private async Task<bool> TrySendAsync(IEmailSender sender, QueuedEmail email, CancellationToken cancellationToken)
    {
        try
        {
            return await sender.SendAsync(email.Message, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Falló el envío del mail \"{Subject}\" ({Error}).",
                email.Message.Subject, exception.GetType().Name);
            return false;
        }
    }
}
