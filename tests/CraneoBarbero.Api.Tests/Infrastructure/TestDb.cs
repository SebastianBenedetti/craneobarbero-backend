using CraneoBarbero.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace CraneoBarbero.Api.Tests.Infrastructure;

public static class TestDb
{
    public static readonly DateTimeOffset Now = new(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);

    public static AppDbContext Create(FakeTimeProvider? time = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"unit-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new TimestampsInterceptor(time ?? new FakeTimeProvider(Now)))
            .Options;
        return new AppDbContext(options);
    }

    public static AppDbContext CreateNpgsqlModelOnly()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model;Username=model;Password=model", DatabaseSetup.ConfigureNpgsql)
            .Options;
        return new AppDbContext(options);
    }
}
