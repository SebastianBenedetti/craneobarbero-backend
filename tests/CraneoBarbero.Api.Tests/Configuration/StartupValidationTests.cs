using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Tests.Configuration;

public sealed class StartupValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("demasiado-corta")]
    public void App_does_not_start_without_a_strong_jwt_secret(string secret)
    {
        using var factory = new OverridingFactory(new() { ["JWT_SECRET"] = secret });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(Unwrap(error), e => e is OptionsValidationException && e.Message.Contains("JWT_SECRET"));
    }

    [Fact]
    public void App_does_not_start_with_an_invalid_schedule()
    {
        using var factory = new OverridingFactory(new() { ["Business:SlotMinutes"] = "7" });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(Unwrap(error), e => e is OptionsValidationException);
    }

    [Fact]
    public void App_does_not_start_without_database_url()
    {
        using var factory = new OverridingFactory(new() { ["DATABASE_URL"] = "" });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(Unwrap(error), e => e.Message.Contains("DATABASE_URL"));
    }

    private static IEnumerable<Exception> Unwrap(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Unwrap))
                {
                    yield return inner;
                }
            }
        }
    }

    private sealed class OverridingFactory(Dictionary<string, string?> overrides) : ApiFactory
    {
        protected override IDictionary<string, string?> Settings
        {
            get
            {
                var settings = base.Settings;
                foreach (var (key, value) in overrides)
                {
                    settings[key] = value;
                }

                return settings;
            }
        }
    }
}
