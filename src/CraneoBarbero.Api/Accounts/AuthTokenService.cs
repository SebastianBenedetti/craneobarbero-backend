using System.Security.Cryptography;
using System.Text;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Accounts;

public sealed class AuthTokenService(AppDbContext db, TimeProvider time)
{
    public static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(1);

    private const int TokenBytes = 32;
    private const int MaxTokenLength = 128;

    public async Task<string> IssueAsync(int userId, AuthTokenType type, CancellationToken cancellationToken)
    {
        var previous = await db.AuthTokens
            .Where(t => t.UserId == userId && t.Type == type && t.UsedAt == null)
            .ToListAsync(cancellationToken);
        db.AuthTokens.RemoveRange(previous);

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        db.AuthTokens.Add(new AuthToken
        {
            UserId = userId,
            Type = type,
            TokenHash = Hash(token),
            ExpiresAt = time.GetUtcNow() + Lifetime(type),
        });
        await db.SaveChangesAsync(cancellationToken);

        return token;
    }

    public Task<bool> WasIssuedRecentlyAsync(int userId, AuthTokenType type, CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow() - ResendCooldown;
        return db.AuthTokens.AnyAsync(t => t.UserId == userId && t.Type == type && t.CreatedAt > since, cancellationToken);
    }

    public async Task<AuthToken?> ConsumeAsync(string? token, AuthTokenType type, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return null;
        }

        var hash = Hash(token.Trim());
        var authToken = await db.AuthTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Type == type, cancellationToken);
        var now = time.GetUtcNow();
        if (authToken is null || authToken.UsedAt is not null || authToken.ExpiresAt <= now)
        {
            return null;
        }

        authToken.UsedAt = now;
        return authToken;
    }

    public async Task DiscardUnusedAsync(int userId, AuthTokenType type, CancellationToken cancellationToken)
    {
        var unused = await db.AuthTokens
            .Where(t => t.UserId == userId && t.Type == type && t.UsedAt == null)
            .ToListAsync(cancellationToken);
        db.AuthTokens.RemoveRange(unused.Where(t => t.UsedAt is null));
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static TimeSpan Lifetime(AuthTokenType type) => type switch
    {
        AuthTokenType.EmailVerification => VerificationLifetime,
        AuthTokenType.PasswordReset => PasswordResetLifetime,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
