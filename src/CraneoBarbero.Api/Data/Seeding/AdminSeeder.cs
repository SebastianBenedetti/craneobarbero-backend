using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Data.Seeding;

public enum AdminSeedResult
{
    AdminAlreadyExists,
    MissingCredentials,
    InvalidCredentials,
    EmailInUse,
    Created,
}

public sealed class AdminSeeder(AppDbContext db, IPasswordHasher hasher, IConfiguration configuration)
{
    public const int MinimumPasswordLength = 12;

    public async Task<AdminSeedResult> SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(u => u.Role == UserRole.Admin && u.IsActive, cancellationToken))
        {
            return AdminSeedResult.AdminAlreadyExists;
        }

        var email = configuration[EnvironmentVariables.AdminEmail];
        var password = configuration[EnvironmentVariables.AdminPassword];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return AdminSeedResult.MissingCredentials;
        }

        email = User.NormalizeEmail(email);
        if (!email.Contains('@') || password.Length < MinimumPasswordLength)
        {
            return AdminSeedResult.InvalidCredentials;
        }

        if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            return AdminSeedResult.EmailInUse;
        }

        db.Users.Add(new User
        {
            Name = "Administrador",
            Email = email,
            PasswordHash = hasher.Hash(password),
            Role = UserRole.Admin,
            IsActive = true,
            EmailVerified = true,
        });
        await db.SaveChangesAsync(cancellationToken);
        return AdminSeedResult.Created;
    }
}

public sealed class AdminSeedHostedService(IServiceScopeFactory scopes, ILogger<AdminSeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<AdminSeeder>().SeedAsync(cancellationToken);
            switch (result)
            {
                case AdminSeedResult.Created:
                    logger.LogInformation("Se creó el admin inicial desde {Variable}.", EnvironmentVariables.AdminEmail);
                    break;
                case AdminSeedResult.MissingCredentials:
                    logger.LogWarning("No hay ningún admin activo y faltan {Email} o {Password}.",
                        EnvironmentVariables.AdminEmail, EnvironmentVariables.AdminPassword);
                    break;
                case AdminSeedResult.InvalidCredentials:
                    logger.LogWarning("No se creó el admin inicial: {Email} no es válido o {Password} tiene menos de {Length} caracteres.",
                        EnvironmentVariables.AdminEmail, EnvironmentVariables.AdminPassword, AdminSeeder.MinimumPasswordLength);
                    break;
                case AdminSeedResult.EmailInUse:
                    logger.LogWarning("No se creó el admin inicial: el email de {Variable} ya pertenece a otro usuario.",
                        EnvironmentVariables.AdminEmail);
                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "No se pudo verificar el admin inicial. Revisá que las migraciones estén aplicadas.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
