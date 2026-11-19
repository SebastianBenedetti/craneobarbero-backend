using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;

namespace CraneoBarbero.Api.Accounts;

public static partial class AccountRules
{
    public const int PasswordMinLength = 8;
    public const int PasswordMaxBytes = 72;
    public const string PasswordPolicyMessage =
        "La contraseña debe tener entre 8 y 72 caracteres, con al menos una letra y un número.";

    public static bool IsStrongPassword(string? password) =>
        password is not null &&
        password.Length >= PasswordMinLength &&
        Encoding.UTF8.GetByteCount(password) <= PasswordMaxBytes &&
        password.Any(char.IsLetter) &&
        password.Any(char.IsDigit) &&
        !string.IsNullOrWhiteSpace(password);

    public static IRuleBuilderOptions<T, string?> StrongPassword<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Ingresá una contraseña.")
            .Must(IsStrongPassword).WithMessage(PasswordPolicyMessage);

    public static IRuleBuilderOptions<T, string?> PersonName<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Ingresá tu nombre.")
            .Must(name => name is not null && name.Trim().Length is >= 2 and <= 100)
            .WithMessage("El nombre debe tener entre 2 y 100 caracteres.");

    public static IRuleBuilderOptions<T, string?> EmailAddress<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Ingresá tu email.")
            .MaximumLength(254).WithMessage("El email es demasiado largo.")
            .Must(email => email is not null && EmailPattern().IsMatch(email.Trim()))
            .WithMessage("El email no es válido.");

    public static IRuleBuilderOptions<T, string?> OptionalPhone<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(phone => string.IsNullOrWhiteSpace(phone) || PhonePattern().IsMatch(phone.Trim()))
            .WithMessage("El teléfono solo puede tener números, espacios y los signos + - ( ), entre 6 y 30 caracteres.");

    public static IRuleBuilderOptions<T, string?> OptionalImageUrl<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(url => string.IsNullOrWhiteSpace(url) ||
                         (url.Trim().Length <= 500 &&
                          Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
                          (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
            .WithMessage("La imagen debe ser una dirección web (http o https) de hasta 500 caracteres.");

    public static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[0-9+()\-\s]{6,30}$")]
    private static partial Regex PhonePattern();
}
