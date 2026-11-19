using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using FluentValidation;

namespace CraneoBarbero.Api.Appointments;

public sealed record CreateAppointmentRequest(int? BarberId, int? CutId, DateTimeOffset? AppointmentDate, int? BranchId);

public sealed record CreateManualAppointmentRequest(
    int? BarberId,
    int? CutId,
    DateTimeOffset? AppointmentDate,
    int? ClientId,
    string? ClientName,
    PaymentMethod? PaymentMethod);

public sealed record AppointmentListQuery(
    int? Page,
    int? PageSize,
    AppointmentStatus? Status,
    DateOnly? From,
    DateOnly? To,
    int? BarberId,
    int? BranchId);

public sealed record AppointmentPersonResponse(int Id, string Name, string? Email, string? Phone);

public sealed record AppointmentCutResponse(int Id, string Name, int DurationMinutes);

public sealed record AppointmentBranchResponse(int Id, string Name, string Address, string? Phone);

public sealed record AppointmentResponse(
    int Id,
    DateTimeOffset AppointmentDate,
    DateTimeOffset EndDate,
    AppointmentStatus Status,
    PaymentStatus PaymentStatus,
    PaymentMethod? PaymentMethod,
    DateTimeOffset? PaymentExpiresAt,
    DateTimeOffset? PaidAt,
    decimal Price,
    AppointmentPersonResponse? Client,
    string? ClientName,
    AppointmentPersonResponse Barber,
    AppointmentCutResponse Cut,
    AppointmentBranchResponse Branch,
    UserRole CreatedByRole,
    DateTimeOffset? CancelledAt,
    UserRole? CancelledByRole,
    string? CancellationReason,
    DateTimeOffset CreatedAt)
{
    public static AppointmentResponse From(Appointment a) => new(
        a.Id,
        a.AppointmentDate,
        a.EndDate,
        a.Status,
        a.PaymentStatus,
        a.PaymentMethod,
        a.PaymentExpiresAt,
        a.PaidAt,
        a.Price,
        a.Client is null ? null : new AppointmentPersonResponse(a.Client.Id, a.Client.Name, a.Client.Email, a.Client.Phone),
        a.ClientName,
        new AppointmentPersonResponse(a.Barber!.Id, a.Barber.Name, null, null),
        new AppointmentCutResponse(a.Cut!.Id, a.Cut.Name, a.Cut.DurationMinutes),
        new AppointmentBranchResponse(a.Branch!.Id, a.Branch.Name, a.Branch.Address, a.Branch.Phone),
        a.CreatedByRole,
        a.CancelledAt,
        a.CancelledByRole,
        a.CancellationReason,
        a.CreatedAt);
}

public sealed record CreatedAppointmentResponse(AppointmentResponse Appointment, string? CheckoutUrl);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record ClientSearchResult(int Id, string Name, string MaskedEmail);

public static class AppointmentLimits
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int ClientSearchMinLength = 3;
    public const int ClientSearchMaxResults = 10;

    public static readonly PaymentMethod[] ManualPaymentMethods =
        [PaymentMethod.Cash, PaymentMethod.Card, PaymentMethod.Transfer, PaymentMethod.Wallet];
}

public sealed class CreateAppointmentRequestValidator : AbstractValidator<CreateAppointmentRequest>
{
    public CreateAppointmentRequestValidator()
    {
        RuleFor(r => r.BarberId).NotNull().WithMessage("Elegí un barbero.").GreaterThan(0).WithMessage("Elegí un barbero.");
        RuleFor(r => r.CutId).NotNull().WithMessage("Elegí un servicio.").GreaterThan(0).WithMessage("Elegí un servicio.");
        RuleFor(r => r.AppointmentDate).NotNull().WithMessage("Elegí la fecha y el horario.");
        RuleFor(r => r.BranchId).GreaterThan(0).When(r => r.BranchId is not null).WithMessage("La sucursal no es válida.");
    }
}

public sealed class CreateManualAppointmentRequestValidator : AbstractValidator<CreateManualAppointmentRequest>
{
    public CreateManualAppointmentRequestValidator()
    {
        RuleFor(r => r.BarberId).GreaterThan(0).When(r => r.BarberId is not null).WithMessage("El barbero no es válido.");
        RuleFor(r => r.CutId).NotNull().WithMessage("Elegí un servicio.").GreaterThan(0).WithMessage("Elegí un servicio.");
        RuleFor(r => r.AppointmentDate).NotNull().WithMessage("Elegí la fecha y el horario.");
        RuleFor(r => r.ClientId).GreaterThan(0).When(r => r.ClientId is not null).WithMessage("El cliente no es válido.");
        RuleFor(r => r.ClientName)
            .Must(name => !string.IsNullOrWhiteSpace(name)).When(r => r.ClientId is null)
            .WithMessage("Ingresá el nombre del cliente o elegí un cliente registrado.");
        RuleFor(r => r.ClientName)
            .Must(name => name is null || name.Trim().Length is >= 2 and <= 100).When(r => r.ClientId is null)
            .WithMessage("El nombre debe tener entre 2 y 100 caracteres.");
        RuleFor(r => r.PaymentMethod).NotNull().WithMessage("Elegí el medio de pago.")
            .Must(method => method is null || AppointmentLimits.ManualPaymentMethods.Contains(method.Value))
            .WithMessage("El medio de pago debe ser efectivo, tarjeta, transferencia o billetera virtual.");
    }
}

public sealed class AppointmentListQueryValidator : AbstractValidator<AppointmentListQuery>
{
    public AppointmentListQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThan(0).When(q => q.Page is not null).WithMessage("La página debe ser 1 o más.");
        RuleFor(q => q.PageSize).InclusiveBetween(1, AppointmentLimits.MaxPageSize).When(q => q.PageSize is not null)
            .WithMessage($"El tamaño de página debe estar entre 1 y {AppointmentLimits.MaxPageSize}.");
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From).When(q => q.From is not null && q.To is not null)
            .WithMessage("La fecha final no puede ser anterior a la inicial.");
    }
}

public static class EmailMask
{
    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0)
        {
            return "***";
        }

        var local = email[..at];
        var visible = local.Length <= 2 ? local[..1] : local[..2];
        return $"{visible}***{email[at..]}";
    }
}

internal static class AppointmentText
{
    public static string? CleanName(string? name) => AccountRules.TrimToNull(name);
}
