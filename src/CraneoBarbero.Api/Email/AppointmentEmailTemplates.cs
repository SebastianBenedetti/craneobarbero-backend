using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace CraneoBarbero.Api.Email;

public sealed record AppointmentEmailDetails(
    string ClientName,
    DateTimeOffset LocalStart,
    string BranchName,
    string BranchAddress,
    string? BranchPhone,
    string BarberName,
    string CutName);

public sealed record EmailLink(string Label, string Url);

public static class AppointmentEmailTemplates
{
    private static readonly string[] Days = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];

    private static readonly string[] Months =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    public static string FormatDate(DateTimeOffset local) =>
        $"{Days[(int)local.DayOfWeek]} {local.Day} de {Months[local.Month - 1]} de {local.Year}, {local.ToString("HH:mm", CultureInfo.InvariantCulture)} h";

    public static string FormatMoney(decimal amount) =>
        "$" + amount.ToString("#,0.##", CultureInfo.InvariantCulture)
            .Replace(',', '_').Replace('.', ',').Replace('_', '.');

    public static EmailMessage Confirmation(string toEmail, AppointmentEmailDetails details, string? paymentLine, IReadOnlyList<EmailLink> links, EmailAttachment? calendar) =>
        Build(
            toEmail,
            details.ClientName,
            "Turno confirmado - Cráneo Barbero",
            $"{details.ClientName}, confirmaste tu turno en Cráneo Barbero",
            DetailLines(details, "Fecha y hora"),
            paymentLine is null ? [] : [paymentLine],
            links,
            "Si no podés venir, cancelalo desde \"Mis turnos\" con al menos 4 horas de anticipación.",
            calendar);

    public static EmailMessage Modification(string toEmail, AppointmentEmailDetails details, IReadOnlyList<EmailLink> links, EmailAttachment? calendar) =>
        Build(
            toEmail,
            details.ClientName,
            "Tu turno cambió - Cráneo Barbero",
            $"{details.ClientName}, tu turno en Cráneo Barbero se modificó",
            DetailLines(details, "Nueva fecha y hora"),
            [],
            links,
            "Si lo habías agregado a tu calendario, volvé a agregarlo con los botones de arriba y borrá el anterior.",
            calendar);

    public static EmailMessage Reminder(string toEmail, AppointmentEmailDetails details, IReadOnlyList<EmailLink> links, EmailAttachment? calendar) =>
        Build(
            toEmail,
            details.ClientName,
            "Recordatorio de tu turno - Cráneo Barbero",
            $"{details.ClientName}, te recordamos tu turno en Cráneo Barbero",
            DetailLines(details, "Fecha y hora"),
            [],
            links,
            "Si no podés venir, avisanos cancelándolo desde \"Mis turnos\".",
            calendar);

    public static EmailMessage Cancellation(string toEmail, AppointmentEmailDetails details, string headline, IReadOnlyList<string> extraLines, EmailAttachment? calendar) =>
        Build(
            toEmail,
            details.ClientName,
            "Turno cancelado - Cráneo Barbero",
            headline,
            DetailLines(details, "Fecha y hora"),
            extraLines,
            [],
            "Podés reservar un nuevo turno cuando quieras desde la página de Cráneo Barbero.",
            calendar);

    private static List<string> DetailLines(AppointmentEmailDetails details, string dateLabel)
    {
        var lines = new List<string>
        {
            $"{dateLabel}: {FormatDate(details.LocalStart)}",
            $"Dirección: {details.BranchAddress} ({details.BranchName})",
        };
        if (!string.IsNullOrWhiteSpace(details.BranchPhone))
        {
            lines.Add($"Número de contacto: {details.BranchPhone}");
        }

        lines.Add($"Servicio: {details.CutName}");
        lines.Add($"Barbero: {details.BarberName}");
        return lines;
    }

    private static EmailMessage Build(
        string toEmail,
        string toName,
        string subject,
        string headline,
        IReadOnlyList<string> details,
        IReadOnlyList<string> extraLines,
        IReadOnlyList<EmailLink> links,
        string note,
        EmailAttachment? calendar)
    {
        var html = HtmlEncoder.Default;
        var detailHtml = string.Concat(details.Select(line =>
            $"<p style=\"margin:0 0 8px;font-size:15px;line-height:1.5;\">{html.Encode(line)}</p>"));
        var extraHtml = string.Concat(extraLines.Select(line =>
            $"<p style=\"margin:16px 0 0;font-size:15px;line-height:1.5;\">{html.Encode(line)}</p>"));
        var linkHtml = string.Concat(links.Select(link =>
            $"<a href=\"{html.Encode(link.Url)}\" style=\"display:inline-block;margin:0 8px 8px 0;background:#ffc831;color:#1e1e1e;text-decoration:none;font-weight:bold;padding:10px 18px;border-radius:6px;\">{html.Encode(link.Label)}</a>"));
        var attachmentNote = links.Count > 0
            ? "<p style=\"margin:16px 0 0;font-size:13px;color:#555555;\">Si usás Google Calendar (Android o computadora), tocá \"Google Calendar\"; en iPhone, \"Agregar al calendario\".</p>"
            : string.Empty;

        var htmlBody = $"""
            <!DOCTYPE html>
            <html lang="es">
            <head><meta charset="utf-8"><title>{html.Encode(subject)}</title></head>
            <body style="margin:0;padding:24px;background:#f4f4f4;font-family:Arial,Helvetica,sans-serif;color:#1e1e1e;">
              <div style="max-width:520px;margin:0 auto;background:#ffffff;border-radius:8px;padding:32px;">
                <h1 style="margin:0 0 24px;font-size:20px;">Cráneo Barbero</h1>
                <p style="margin:0 0 20px;font-size:17px;font-weight:bold;line-height:1.4;">{html.Encode(headline)}</p>
                {detailHtml}
                {extraHtml}
                {(links.Count > 0 ? $"<div style=\"margin:24px 0 0;\">{linkHtml}</div>" : string.Empty)}
                {attachmentNote}
                <p style="margin:24px 0 0;font-size:13px;color:#555555;">{html.Encode(note)}</p>
              </div>
            </body>
            </html>
            """;

        var text = new StringBuilder();
        text.AppendLine(headline).AppendLine();
        foreach (var line in details) text.AppendLine(line);
        foreach (var line in extraLines) text.AppendLine().AppendLine(line);
        if (links.Count > 0)
        {
            text.AppendLine();
            foreach (var link in links) text.AppendLine($"{link.Label}: {link.Url}");
        }

        text.AppendLine().AppendLine(note).AppendLine().Append("Cráneo Barbero");

        return new EmailMessage(toEmail, toName, subject, htmlBody, text.ToString(),
            calendar is null ? null : [calendar]);
    }
}
