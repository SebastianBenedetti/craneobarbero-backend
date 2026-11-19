using System.Net.Http.Headers;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CraneoBarbero.Api.Tests.Infrastructure;

public class ApiFactory : WebApplicationFactory<Program>
{
    public const string TestSecret = "test-secret-only-for-automated-tests-0123456789";
    public const string AllowedOrigin = "https://frontend.example.com";

    private readonly string _databaseName = $"tests-{Guid.NewGuid():N}";

    protected virtual IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["JWT_SECRET"] = TestSecret,
        ["DATABASE_URL"] = "postgres://tester:not-a-real-password@localhost:5432/tests",
        ["FRONTEND_URL"] = AllowedOrigin,
        ["Security:MaxRequestBodyBytes"] = "2048",
        ["Security:GlobalRateLimit:PermitLimit"] = "10000",
        ["Security:AuthRateLimit:PermitLimit"] = "1000",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>((provider, options) => options
                .UseInMemoryDatabase(_databaseName)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .AddInterceptors(provider.GetRequiredService<TimestampsInterceptor>()));

            services.AddControllers().AddApplicationPart(typeof(ApiFactory).Assembly);
            services.AddScoped<IValidator<EchoRequest>, EchoRequestValidator>();
        });
    }

    public async Task<User> AddUserAsync(UserRole role, bool isActive = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Name = "Persona de prueba",
            Email = $"user-{Guid.NewGuid():N}@example.com",
            PasswordHash = "$2b$12$abcdefghijklmnopqrstuuS3mZ3zvXnmQbgLSzMNFyK3x0BxhwJqq",
            Role = role,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task UpdateUserAsync(int id, Action<User> change)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        change(user);
        await db.SaveChangesAsync();
    }

    public string TokenFor(User user) => Services.GetRequiredService<IJwtTokenService>().CreateToken(user).Token;

    public HttpClient ClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<HttpClient> ClientForAsync(UserRole role)
    {
        var user = await AddUserAsync(role);
        return ClientWithToken(TokenFor(user));
    }
}
