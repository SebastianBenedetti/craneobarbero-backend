using CraneoBarbero.Api.Accounts;
using FluentValidation;

namespace CraneoBarbero.Api.Catalog;

public sealed record BranchRequest(string? Name, string? Address, string? Phone, string? ImageUrl);

public sealed record CutRequest(string? Name, decimal? Price, int? DurationMinutes, string? Description);

public sealed record CreateBarberRequest(
    string? Name,
    string? Email,
    string? Password,
    int? BranchId,
    DateOnly? StartDate,
    string? ImageUrl);

public sealed record UpdateBarberRequest(
    string? Name,
    int? BranchId,
    DateOnly? StartDate,
    string? ImageUrl,
    string? NewPassword,
    bool? Confirm = null);

public static class CatalogLimits
{
    public const decimal MaxPrice = 99_999_999.99m;
    public const int MaxDurationMinutes = 480;
    public const int MaxDescriptionLength = 500;
}

public sealed class BranchRequestValidator : AbstractValidator<BranchRequest>
{
    public BranchRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().WithMessage("Ingresá el nombre de la sucursal.")
            .Must(name => name is not null && name.Trim().Length is >= 2 and <= 100)
            .WithMessage("El nombre debe tener entre 2 y 100 caracteres.");
        RuleFor(r => r.Address).NotEmpty().WithMessage("Ingresá la dirección.")
            .Must(address => address is not null && address.Trim().Length is >= 3 and <= 200)
            .WithMessage("La dirección debe tener entre 3 y 200 caracteres.");
        RuleFor(r => r.Phone).OptionalPhone();
        RuleFor(r => r.ImageUrl).OptionalImageUrl();
    }
}

public sealed class CutRequestValidator : AbstractValidator<CutRequest>
{
    public CutRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().WithMessage("Ingresá el nombre del servicio.")
            .Must(name => name is not null && name.Trim().Length is >= 2 and <= 100)
            .WithMessage("El nombre debe tener entre 2 y 100 caracteres.");
        RuleFor(r => r.Price).NotNull().WithMessage("Ingresá el precio.")
            .InclusiveBetween(0m, CatalogLimits.MaxPrice).WithMessage("El precio no puede ser negativo ni tan alto.")
            .Must(price => price is null || decimal.Round(price.Value, 2) == price.Value)
            .WithMessage("El precio admite hasta dos decimales.");
        RuleFor(r => r.DurationMinutes).NotNull().WithMessage("Ingresá la duración en minutos.")
            .InclusiveBetween(1, CatalogLimits.MaxDurationMinutes)
            .WithMessage($"La duración debe estar entre 1 y {CatalogLimits.MaxDurationMinutes} minutos.");
        RuleFor(r => r.Description).MaximumLength(CatalogLimits.MaxDescriptionLength)
            .WithMessage($"La descripción admite hasta {CatalogLimits.MaxDescriptionLength} caracteres.");
    }
}

public sealed class CreateBarberRequestValidator : AbstractValidator<CreateBarberRequest>
{
    public CreateBarberRequestValidator(TimeProvider time)
    {
        RuleFor(r => r.Name).PersonName();
        RuleFor(r => r.Email).EmailAddress();
        RuleFor(r => r.Password).StrongPassword();
        RuleFor(r => r.BranchId).NotNull().WithMessage("Elegí la sucursal del barbero.")
            .GreaterThan(0).WithMessage("Elegí la sucursal del barbero.");
        RuleFor(r => r.StartDate).StartDate(time);
        RuleFor(r => r.ImageUrl).OptionalImageUrl();
    }
}

public sealed class UpdateBarberRequestValidator : AbstractValidator<UpdateBarberRequest>
{
    public UpdateBarberRequestValidator(TimeProvider time)
    {
        RuleFor(r => r.Name).PersonName();
        RuleFor(r => r.BranchId).NotNull().WithMessage("Elegí la sucursal del barbero.")
            .GreaterThan(0).WithMessage("Elegí la sucursal del barbero.");
        RuleFor(r => r.StartDate).StartDate(time);
        RuleFor(r => r.ImageUrl).OptionalImageUrl();
        RuleFor(r => r.NewPassword).StrongPassword().When(r => !string.IsNullOrEmpty(r.NewPassword));
    }
}

internal static class CatalogRuleExtensions
{
    public static IRuleBuilderOptions<T, DateOnly?> StartDate<T>(this IRuleBuilder<T, DateOnly?> rule, TimeProvider time) =>
        rule.Must(date => date is null ||
                          (date.Value.Year >= 1950 &&
                           date.Value <= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1)))
            .WithMessage("La fecha de inicio no puede ser futura ni anterior a 1950.");
}
