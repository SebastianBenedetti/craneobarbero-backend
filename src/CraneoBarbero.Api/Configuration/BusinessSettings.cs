using CraneoBarbero.Api.Domain.Enums;

namespace CraneoBarbero.Api.Configuration;

public sealed class BusinessSettings
{
    public const string SectionName = "Business";

    public string TimeZone { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public int SlotMinutes { get; set; }
    public int CancellationWindowHours { get; set; }
    public int PaymentExpirationMinutes { get; set; }
    public int BookingWindowDays { get; set; }
    public int SlotGenerationMonths { get; set; }
    public List<PaymentMethod> PaymentMethods { get; set; } = [];
    public Dictionary<DayOfWeek, List<OpeningRange>> OpeningHours { get; set; } = [];

    public IReadOnlyList<OpeningRange> GetOpeningRanges(DayOfWeek day) =>
        OpeningHours.TryGetValue(day, out var ranges) ? ranges.OrderBy(r => r.Start).ToList() : [];
}

public sealed class OpeningRange
{
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
}
