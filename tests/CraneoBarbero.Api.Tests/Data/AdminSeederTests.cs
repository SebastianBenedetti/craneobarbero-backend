using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Data.Seeding;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CraneoBarbero.Api.Tests.Data;

public sealed class AdminSeederTests
{
    private const string Password = "Clave-segura-de-prueba-2026";
    private readonly BcryptPasswordHasher _hasher = new();

    [Fact]
    public async Task Creates_the_initial_admin_from_environment_variables()
    {
        await using var db = TestDb.Create();

        var result = await Seeder(db, "  Admin@Example.com ", Password).SeedAsync(CancellationToken.None);

        Assert.Equal(AdminSeedResult.Created, result);
        var admin = await db.Users.SingleAsync();
        Assert.Equal("admin@example.com", admin.Email);
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.True(admin.IsActive);
        Assert.True(admin.EmailVerified);
        Assert.Null(admin.BranchId);
        Assert.NotEqual(Password, admin.PasswordHash);
        Assert.True(_hasher.Verify(Password, admin.PasswordHash));
    }

    [Fact]
    public async Task Does_nothing_when_an_active_admin_exists()
    {
        await using var db = TestDb.Create();
        db.Users.Add(new User { Name = "Admin", Email = "otro@example.com", PasswordHash = "x", Role = UserRole.Admin });
        await db.SaveChangesAsync();

        var result = await Seeder(db, "admin@example.com", Password).SeedAsync(CancellationToken.None);

        Assert.Equal(AdminSeedResult.AdminAlreadyExists, result);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Runs_only_once()
    {
        await using var db = TestDb.Create();
        var seeder = Seeder(db, "admin@example.com", Password);

        await seeder.SeedAsync(CancellationToken.None);
        var second = await seeder.SeedAsync(CancellationToken.None);

        Assert.Equal(AdminSeedResult.AdminAlreadyExists, second);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Theory]
    [InlineData(null, Password, AdminSeedResult.MissingCredentials)]
    [InlineData("admin@example.com", null, AdminSeedResult.MissingCredentials)]
    [InlineData("admin@example.com", "corta", AdminSeedResult.InvalidCredentials)]
    [InlineData("no-es-un-email", Password, AdminSeedResult.InvalidCredentials)]
    public async Task Rejects_missing_or_weak_credentials(string? email, string? password, AdminSeedResult expected)
    {
        await using var db = TestDb.Create();

        var result = await Seeder(db, email, password).SeedAsync(CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.False(await db.Users.AnyAsync());
    }

    [Fact]
    public async Task Never_takes_over_an_existing_account()
    {
        await using var db = TestDb.Create();
        db.Users.Add(new User { Name = "Cliente", Email = "admin@example.com", PasswordHash = "x", Role = UserRole.Client });
        await db.SaveChangesAsync();

        var result = await Seeder(db, "admin@example.com", Password).SeedAsync(CancellationToken.None);

        Assert.Equal(AdminSeedResult.EmailInUse, result);
        Assert.Equal(UserRole.Client, (await db.Users.SingleAsync()).Role);
    }

    [Fact]
    public async Task Api_startup_creates_the_admin()
    {
        await using var factory = new SeededFactory();
        factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = await db.Users.SingleAsync();
        Assert.Equal("seed@example.com", admin.Email);
        Assert.Equal(UserRole.Admin, admin.Role);
    }

    private AdminSeeder Seeder(AppDbContext db, string? email, string? password)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ADMIN_EMAIL"] = email, ["ADMIN_PASSWORD"] = password })
            .Build();
        return new AdminSeeder(db, _hasher, configuration);
    }

    private sealed class SeededFactory : ApiFactory
    {
        protected override IDictionary<string, string?> Settings
        {
            get
            {
                var settings = base.Settings;
                settings["ADMIN_EMAIL"] = "seed@example.com";
                settings["ADMIN_PASSWORD"] = Password;
                return settings;
            }
        }
    }
}
