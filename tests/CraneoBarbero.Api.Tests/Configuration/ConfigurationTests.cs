using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace CraneoBarbero.Api.Tests.Configuration;

public sealed class ConfigurationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private BusinessSettings Business => factory.Services.GetRequiredService<IOptions<BusinessSettings>>().Value;

    [Fact]
    public void Business_rules_are_loaded_from_central_configuration()
    {
        var settings = Business;

        Assert.Equal("America/Argentina/Buenos_Aires", settings.TimeZone);
        Assert.Equal("ARS", settings.Currency);
        Assert.Equal(30, settings.SlotMinutes);
        Assert.Equal(4, settings.CancellationWindowHours);
        Assert.Equal(30, settings.PaymentExpirationMinutes);
        Assert.Equal(60, settings.BookingWindowDays);
        Assert.Equal(12, settings.SlotGenerationMonths);
        Assert.Equal(
            [PaymentMethod.Cash, PaymentMethod.Card, PaymentMethod.Transfer, PaymentMethod.Wallet, PaymentMethod.MercadoPago],
            settings.PaymentMethods);
    }

    [Fact]
    public void Opening_hours_match_the_base_schedule()
    {
        var settings = Business;

        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
        {
            var ranges = settings.GetOpeningRanges(day);
            Assert.Equal(2, ranges.Count);
            Assert.Equal((new TimeOnly(9, 0), new TimeOnly(13, 0)), (ranges[0].Start, ranges[0].End));
            Assert.Equal((new TimeOnly(15, 0), new TimeOnly(20, 0)), (ranges[1].Start, ranges[1].End));
        }

        var saturday = Assert.Single(settings.GetOpeningRanges(DayOfWeek.Saturday));
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(15, 0)), (saturday.Start, saturday.End));
        Assert.Empty(settings.GetOpeningRanges(DayOfWeek.Sunday));
    }

    [Fact]
    public void Validator_accepts_the_shipped_configuration()
    {
        Assert.True(new BusinessSettingsValidator().Validate(null, Business).Succeeded);
    }

    [Fact]
    public void Validator_rejects_inconsistent_schedules()
    {
        var settings = new BusinessSettings
        {
            TimeZone = "Zona/Inexistente",
            Currency = "ARS",
            SlotMinutes = 30,
            PaymentExpirationMinutes = 30,
            BookingWindowDays = 60,
            SlotGenerationMonths = 12,
            PaymentMethods = [PaymentMethod.Cash],
            OpeningHours = new()
            {
                [DayOfWeek.Monday] =
                [
                    new OpeningRange { Start = new TimeOnly(9, 0), End = new TimeOnly(13, 15) },
                    new OpeningRange { Start = new TimeOnly(12, 0), End = new TimeOnly(11, 0) },
                ],
            },
        };

        var result = new BusinessSettingsValidator().Validate(null, settings);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("zona horaria"));
        Assert.Contains(result.Failures!, f => f.Contains("grilla"));
        Assert.Contains(result.Failures!, f => f.Contains("superponen"));
        Assert.Contains(result.Failures!, f => f.Contains("termina antes"));
    }

    [Fact]
    public void Business_clock_converts_between_utc_and_buenos_aires()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 2, 30, 0, TimeSpan.Zero));
        var clock = new BusinessClock(time, Options.Create(Business));

        Assert.Equal(new DateOnly(2026, 11, 18), clock.Today);
        Assert.Equal(TimeSpan.FromHours(-3), clock.Now.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero),
            clock.ToUtc(new DateOnly(2026, 11, 19), new TimeOnly(9, 0)));
    }

    [Fact]
    public void Database_url_is_converted_with_verified_ssl()
    {
        var connection = new NpgsqlConnectionStringBuilder(DatabaseConnection.BuildConnectionString(
            "postgresql://usuario.proyecto:clave%40con%3Asimbolos@db.example.com:5432/postgres", "/certs/ca.crt"));

        Assert.Equal("db.example.com", connection.Host);
        Assert.Equal(5432, connection.Port);
        Assert.Equal("postgres", connection.Database);
        Assert.Equal("usuario.proyecto", connection.Username);
        Assert.Equal("clave@con:simbolos", connection.Password);
        Assert.Equal(SslMode.VerifyFull, connection.SslMode);
        Assert.Equal("/certs/ca.crt", connection.RootCertificate);
    }

    [Fact]
    public void Key_value_connection_strings_are_forced_to_verified_ssl()
    {
        var connection = new NpgsqlConnectionStringBuilder(DatabaseConnection.BuildConnectionString(
            "Host=db.example.com;Database=app;Username=u;Password=p;SSL Mode=Disable", null));

        Assert.Equal(SslMode.VerifyFull, connection.SslMode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("postgresql://")]
    public void Missing_or_invalid_database_url_fails(string? value)
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseConnection.BuildConnectionString(value, null));
    }

    [Fact]
    public void Frontend_origins_are_parsed_strictly()
    {
        var origins = CorsSettings.ParseOrigins("https://app.example.com/, http://localhost:5173 ; ftp://x.com, no-url, https://a.com/ruta");

        Assert.Equal(["https://app.example.com", "http://localhost:5173"], origins);
    }

    [Fact]
    public void Root_certificate_path_resolves_inside_the_application()
    {
        var connection = new NpgsqlConnectionStringBuilder(
            factory.Services.GetRequiredService<IOptions<DatabaseSettings>>().Value.ConnectionString);

        Assert.Equal(SslMode.VerifyFull, connection.SslMode);
        Assert.True(Path.IsPathRooted(connection.RootCertificate));
        Assert.EndsWith(Path.Combine("certs", "supabase-root-2021.crt"), connection.RootCertificate);
        Assert.True(File.Exists(connection.RootCertificate));
    }
}
