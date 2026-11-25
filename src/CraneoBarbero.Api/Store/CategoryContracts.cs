using CraneoBarbero.Api.Domain.Entities;
using FluentValidation;

namespace CraneoBarbero.Api.Store;

public sealed record CategoryRequest(string? Name);

public sealed record CategoryResponse(int Id, string Name)
{
    public static CategoryResponse From(ProductCategory category) => new(category.Id, category.Name);
}

public sealed class CategoryRequestValidator : AbstractValidator<CategoryRequest>
{
    public CategoryRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().WithMessage("Ingresá el nombre de la categoría.")
            .Must(name => name is not null && name.Trim().Length is >= 2 and <= 100)
            .WithMessage("El nombre debe tener entre 2 y 100 caracteres.");
    }
}
