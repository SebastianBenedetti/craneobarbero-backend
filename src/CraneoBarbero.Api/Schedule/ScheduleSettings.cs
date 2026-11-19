using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Schedule;

public sealed class ScheduleSettings
{
    public const string SectionName = "Schedule";

    public bool JobsEnabled { get; set; } = true;
    public string HolidaysApiUrl { get; set; } = string.Empty;
    public int HolidaysApiTimeoutSeconds { get; set; } = 20;
    public int HolidaySyncIntervalDays { get; set; } = 7;
    public TimeOnly DailyGenerationTime { get; set; } = new(0, 30);
    public int PastSlotRetentionDays { get; set; } = 90;
}

public sealed class ScheduleSettingsValidator : IValidateOptions<ScheduleSettings>
{
    public ValidateOptionsResult Validate(string? name, ScheduleSettings settings)
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(settings.HolidaysApiUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Schedule:HolidaysApiUrl debe ser una dirección https.");
        }

        if (settings.HolidaysApiTimeoutSeconds <= 0) errors.Add("Schedule:HolidaysApiTimeoutSeconds debe ser positivo.");
        if (settings.HolidaySyncIntervalDays <= 0) errors.Add("Schedule:HolidaySyncIntervalDays debe ser positivo.");
        if (settings.PastSlotRetentionDays < 0) errors.Add("Schedule:PastSlotRetentionDays no puede ser negativo.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
