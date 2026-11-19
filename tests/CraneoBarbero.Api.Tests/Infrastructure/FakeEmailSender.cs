using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CraneoBarbero.Api.Email;

namespace CraneoBarbero.Api.Tests.Infrastructure;

public sealed partial class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public bool Fail { get; set; }

    public IReadOnlyCollection<EmailMessage> Sent => _sent.ToArray();

    public Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            return Task.FromResult(false);
        }

        _sent.Enqueue(message);
        return Task.FromResult(true);
    }

    public IReadOnlyList<EmailMessage> SentTo(string email) =>
        _sent.Where(m => m.ToEmail == email).ToList();

    public async Task<EmailMessage> WaitForAsync(string email, int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var messages = SentTo(email);
            if (messages.Count >= count)
            {
                return messages[count - 1];
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"No llegó el mail número {count} a {email}.");
    }

    public static string ExtractToken(EmailMessage message)
    {
        var match = TokenPattern().Match(message.TextBody);
        Assert.True(match.Success, "El mail no tiene un enlace con token.");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [GeneratedRegex(@"token=([^\s&]+)")]
    private static partial Regex TokenPattern();
}
