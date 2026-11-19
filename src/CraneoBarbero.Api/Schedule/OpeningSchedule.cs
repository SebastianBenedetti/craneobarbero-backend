using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Schedule;

public sealed class OpeningSchedule(BusinessClock clock, IOptions<BusinessSettings> options)
{
    public static readonly AppointmentStatus[] BlockingStatuses =
        [AppointmentStatus.AwaitingPayment, AppointmentStatus.Pending, AppointmentStatus.Completed];

    private readonly BusinessSettings _settings = options.Value;

    public int SlotMinutes => _settings.SlotMinutes;

    public TimeSpan SlotLength => TimeSpan.FromMinutes(_settings.SlotMinutes);

    public DateOnly GenerationHorizon(DateOnly today) => today.AddMonths(_settings.SlotGenerationMonths);

    public IEnumerable<DateTimeOffset> BaseSlotStarts(DateOnly date)
    {
        foreach (var range in _settings.GetOpeningRanges(date.DayOfWeek))
        {
            var start = (int)range.Start.ToTimeSpan().TotalMinutes;
            var end = (int)range.End.ToTimeSpan().TotalMinutes;
            for (var minute = start; minute + _settings.SlotMinutes <= end; minute += _settings.SlotMinutes)
            {
                yield return clock.ToUtc(date, TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute)));
            }
        }
    }

    public (DateTimeOffset Start, DateTimeOffset End) DayRange(DateOnly date) =>
        (clock.ToUtc(date, TimeOnly.MinValue), clock.ToUtc(date.AddDays(1), TimeOnly.MinValue));

    public DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(clock.ToBusinessTime(instant).DateTime);

    public TimeOnly TimeOf(DateTimeOffset instant) => TimeOnly.FromDateTime(clock.ToBusinessTime(instant).DateTime);

    public int BlocksFor(int durationMinutes) =>
        (durationMinutes + _settings.SlotMinutes - 1) / _settings.SlotMinutes;
}
