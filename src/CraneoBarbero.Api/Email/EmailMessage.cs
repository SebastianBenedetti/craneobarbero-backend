namespace CraneoBarbero.Api.Email;

public sealed record EmailMessage(string ToEmail, string ToName, string Subject, string HtmlBody, string TextBody);

public interface IEmailSender
{
    Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
