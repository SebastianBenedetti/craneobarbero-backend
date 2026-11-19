using System.Text.Encodings.Web;
using CraneoBarbero.Api.Domain.Entities;

namespace CraneoBarbero.Api.Email;

public static class AccountEmailTemplates
{
    public static EmailMessage Verification(User user, string link) => Build(
        user,
        "Confirmá tu email - Cráneo Barbero",
        "Gracias por registrarte en Cráneo Barbero. Para confirmar tu email y poder reservar turnos, entrá al siguiente enlace.",
        "Confirmar email",
        link,
        "El enlace vence en 24 horas. Si no creaste una cuenta, ignorá este mail.");

    public static EmailMessage PasswordReset(User user, string link) => Build(
        user,
        "Restablecé tu contraseña - Cráneo Barbero",
        "Recibimos un pedido para restablecer la contraseña de tu cuenta de Cráneo Barbero. Para elegir una nueva, entrá al siguiente enlace.",
        "Restablecer contraseña",
        link,
        "El enlace vence en 1 hora y se puede usar una sola vez. Si no lo pediste, ignorá este mail: tu contraseña no cambia.");

    private static EmailMessage Build(User user, string subject, string intro, string action, string link, string note)
    {
        var name = FirstName(user.Name);
        var html = HtmlEncoder.Default;

        var htmlBody = $"""
            <!DOCTYPE html>
            <html lang="es">
            <head><meta charset="utf-8"><title>{html.Encode(subject)}</title></head>
            <body style="margin:0;padding:24px;background:#f4f4f4;font-family:Arial,Helvetica,sans-serif;color:#1e1e1e;">
              <div style="max-width:520px;margin:0 auto;background:#ffffff;border-radius:8px;padding:32px;">
                <h1 style="margin:0 0 24px;font-size:20px;">Cráneo Barbero</h1>
                <p style="margin:0 0 16px;font-size:15px;line-height:1.5;">Hola, {html.Encode(name)}:</p>
                <p style="margin:0 0 24px;font-size:15px;line-height:1.5;">{html.Encode(intro)}</p>
                <p style="margin:0 0 24px;">
                  <a href="{html.Encode(link)}" style="display:inline-block;background:#ffc831;color:#1e1e1e;text-decoration:none;font-weight:bold;padding:12px 24px;border-radius:6px;">{html.Encode(action)}</a>
                </p>
                <p style="margin:0 0 8px;font-size:13px;color:#555555;">Si el botón no funciona, copiá este enlace en el navegador:</p>
                <p style="margin:0 0 24px;font-size:13px;word-break:break-all;"><a href="{html.Encode(link)}" style="color:#1e1e1e;">{html.Encode(link)}</a></p>
                <p style="margin:0;font-size:13px;color:#555555;">{html.Encode(note)}</p>
              </div>
            </body>
            </html>
            """;

        var textBody = $"""
            Hola, {name}:

            {intro}

            {action}: {link}

            {note}

            Cráneo Barbero
            """;

        return new EmailMessage(user.Email, user.Name, subject, htmlBody, textBody);
    }

    private static string FirstName(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }
}
