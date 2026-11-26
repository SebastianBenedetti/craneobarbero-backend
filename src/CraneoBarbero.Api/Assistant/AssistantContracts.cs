using FluentValidation;

namespace CraneoBarbero.Api.Assistant;

public sealed class AssistantSettings
{
    public const string SectionName = "Assistant";

    public int ConversationMinutes { get; set; } = 30;
    public int MessagesPerMinutePerUser { get; set; } = 20;
    public int DaysPerPage { get; set; } = 8;
    public int MaxMessageLength { get; set; } = 300;
    public string GeminiApiUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";
    public string GeminiModel { get; set; } = "gemini-2.5-flash-lite";
    public int GeminiTimeoutSeconds { get; set; } = 10;
    public int AiMessagesPerUserPerDay { get; set; } = 30;
    public int AiMessagesPerMinute { get; set; } = 10;
    public string GeminiApiKey { get; set; } = string.Empty;
}

public sealed record AssistantMessageRequest(string? ConversationId, string? Message, string? Choice);

public sealed class AssistantMessageRequestValidator : AbstractValidator<AssistantMessageRequest>
{
    public AssistantMessageRequestValidator()
    {
        RuleFor(r => r.ConversationId).MaximumLength(64).WithMessage("La conversación no es válida.");
        RuleFor(r => r.Message).MaximumLength(300).WithMessage("El mensaje puede tener hasta 300 caracteres.");
        RuleFor(r => r.Choice).MaximumLength(100).WithMessage("La opción no es válida.");
    }
}

public enum AssistantStep
{
    Start,
    Branch,
    Barber,
    Cut,
    Day,
    Time,
    Confirm,
    Done,
}

public enum DayPart
{
    Morning,
    Afternoon,
}

public static class DayParts
{
    public static readonly TimeOnly AfternoonStart = new(14, 0);

    public static DayPart Of(TimeOnly time) => time < AfternoonStart ? DayPart.Morning : DayPart.Afternoon;
}

public sealed record AssistantOption(string Label, string Value);

public sealed record AssistantSummary(
    string BranchName,
    string BranchAddress,
    string BarberName,
    string CutName,
    int DurationMinutes,
    DateOnly Date,
    string DateLabel,
    string Time,
    DateTimeOffset StartAt,
    decimal Price);

public sealed record AssistantMessageResponse(
    string ConversationId,
    string Reply,
    IReadOnlyList<AssistantOption> Options,
    AssistantStep Step,
    AssistantSummary? Summary,
    string? CheckoutUrl,
    int? AppointmentId);

public sealed class AssistantConversation
{
    public required string Id { get; init; }
    public required int UserId { get; init; }
    public AssistantStep Step { get; set; } = AssistantStep.Start;
    public int? BranchId { get; set; }
    public int? BarberId { get; set; }
    public int? CutId { get; set; }
    public DateOnly? Date { get; set; }
    public DateTimeOffset? StartAt { get; set; }
    public int DayPage { get; set; }
    public DayPart? PreferredPart { get; set; }
    public DateOnly? PendingDate { get; set; }
    public int? AppointmentId { get; set; }
    public string? CheckoutUrl { get; set; }

    public void Reset()
    {
        BranchId = null;
        BarberId = null;
        CutId = null;
        ClearDay();
        PendingDate = null;
        PreferredPart = null;
        AppointmentId = null;
        CheckoutUrl = null;
    }

    public void ClearDay()
    {
        Date = null;
        StartAt = null;
        DayPage = 0;
    }
}

public sealed record CatalogBranch(int Id, string Name, string Address);

public sealed record CatalogBarber(int Id, string Name, int BranchId);

public sealed record CatalogCut(int Id, string Name, decimal Price, int DurationMinutes);

public sealed record AssistantCatalog(
    IReadOnlyList<CatalogBranch> Branches,
    IReadOnlyList<CatalogBarber> Barbers,
    IReadOnlyList<CatalogCut> Cuts);

public sealed record AssistantInterpretation(
    bool OtherIntent = false,
    int? BranchId = null,
    int? BarberId = null,
    int? CutId = null,
    DateOnly? Date = null,
    TimeOnly? Time = null,
    DayPart? Part = null)
{
    public bool HasValues => BranchId is not null || BarberId is not null || CutId is not null ||
                             Date is not null || Time is not null || Part is not null;
}
