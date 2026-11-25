using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Domain.Entities;
using FluentValidation;

namespace CraneoBarbero.Api.Store;

public sealed record SupplierRequest(
    string? Name,
    string? ContactName,
    string? Phone,
    string? Email,
    string? TaxId,
    string? Notes,
    bool? IsActive = null);

public sealed record SupplierResponse(
    int Id,
    string Name,
    string? ContactName,
    string? Phone,
    string? Email,
    string? TaxId,
    string? Notes,
    bool IsActive)
{
    public static SupplierResponse From(Supplier supplier) => new(
        supplier.Id,
        supplier.Name,
        supplier.ContactName,
        supplier.Phone,
        supplier.Email,
        supplier.TaxId,
        supplier.Notes,
        supplier.IsActive);
}

public sealed class SupplierRequestValidator : AbstractValidator<SupplierRequest>
{
    public const int MaxNotesLength = 500;

    public SupplierRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().WithMessage("Ingresá el nombre del proveedor.")
            .Must(name => name is not null && name.Trim().Length is >= 2 and <= 150)
            .WithMessage("El nombre debe tener entre 2 y 150 caracteres.");
        RuleFor(r => r.ContactName)
            .Must(name => string.IsNullOrWhiteSpace(name) || name.Trim().Length is >= 2 and <= 100)
            .WithMessage("El nombre de contacto debe tener entre 2 y 100 caracteres.");
        RuleFor(r => r.Phone).OptionalPhone();
        RuleFor(r => r.Email).EmailAddress().When(r => !string.IsNullOrWhiteSpace(r.Email));
        RuleFor(r => r.TaxId)
            .Must(taxId => string.IsNullOrWhiteSpace(taxId) || taxId.Trim().Length <= 20)
            .WithMessage("El CUIT admite hasta 20 caracteres.");
        RuleFor(r => r.Notes).MaximumLength(MaxNotesLength)
            .WithMessage($"Las notas admiten hasta {MaxNotesLength} caracteres.");
    }
}
