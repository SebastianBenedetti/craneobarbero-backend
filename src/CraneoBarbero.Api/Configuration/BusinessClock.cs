using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Configuration;

public sealed class BusinessClock(TimeProvider timeProvider, IOptions<BusinessSettings> settings)
{
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.Value.TimeZone);

    public TimeZoneInfo TimeZone => _timeZone;

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    public DateTimeOffset Now => ToBusinessTime(UtcNow);

    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    public DateTimeOffset ToBusinessTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, _timeZone);

    public DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var offset = _timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
