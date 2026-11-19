using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Configuration;

public sealed class BusinessSettingsValidator : IValidateOptions<BusinessSettings>
{
    public ValidateOptionsResult Validate(string? name, BusinessSettings settings)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.TimeZone) ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(settings.TimeZone, out _))
        {
            errors.Add($"La zona horaria '{settings.TimeZone}' no existe.");
        }

        if (settings.SlotMinutes <= 0 || 60 % settings.SlotMinutes != 0)
        {
            errors.Add("SlotMinutes debe ser un divisor positivo de 60.");
        }

        if (settings.CancellationWindowHours < 0) errors.Add("CancellationWindowHours no puede ser negativo.");
        if (settings.PaymentExpirationMinutes <= 0) errors.Add("PaymentExpirationMinutes debe ser positivo.");
        if (settings.BookingWindowDays <= 0) errors.Add("BookingWindowDays debe ser positivo.");
        if (settings.SlotGenerationMonths <= 0) errors.Add("SlotGenerationMonths debe ser positivo.");
        if (settings.SlotGenerationMonths * 28 < settings.BookingWindowDays)
        {
            errors.Add("La ventana de reserva no puede superar el período de horarios generados.");
        }

        if (string.IsNullOrWhiteSpace(settings.Currency)) errors.Add("Currency es obligatorio.");
        if (settings.PaymentMethods.Count == 0) errors.Add("PaymentMethods no puede estar vacío.");
        if (settings.PaymentMethods.Distinct().Count() != settings.PaymentMethods.Count)
        {
            errors.Add("PaymentMethods tiene valores repetidos.");
        }

        if (settings.OpeningHours.Values.All(ranges => ranges.Count == 0))
        {
            errors.Add("OpeningHours debe tener al menos una franja abierta.");
        }

        if (settings.SlotMinutes > 0 && 60 % settings.SlotMinutes == 0)
        {
            foreach (var (day, ranges) in settings.OpeningHours)
            {
                ValidateRanges(day, ranges, settings.SlotMinutes, errors);
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateRanges(DayOfWeek day, List<OpeningRange> ranges, int slotMinutes, List<string> errors)
    {
        OpeningRange? previous = null;
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (range.Start >= range.End)
            {
                errors.Add($"{day}: la franja {range.Start}-{range.End} termina antes de empezar.");
            }

            if (!IsAligned(range.Start, slotMinutes) || !IsAligned(range.End, slotMinutes))
            {
                errors.Add($"{day}: la franja {range.Start}-{range.End} no respeta la grilla de {slotMinutes} minutos.");
            }

            if (previous is not null && range.Start < previous.End)
            {
                errors.Add($"{day}: las franjas se superponen.");
            }

            previous = range;
        }
    }

    private static bool IsAligned(TimeOnly time, int slotMinutes) =>
        time.Second == 0 && time.Millisecond == 0 && (time.Hour * 60 + time.Minute) % slotMinutes == 0;
}
