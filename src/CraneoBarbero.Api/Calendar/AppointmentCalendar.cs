using System.Globalization;
using System.Text;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;

namespace CraneoBarbero.Api.Calendar;

public sealed class AppointmentCalendar(BusinessClock clock)
{
    public const string Title = "Turno en Cráneo Barbero";
    public const string FileName = "turno-craneo-barbero.ics";
    public const string ContentType = "text/calendar; charset=utf-8";

    private static readonly DateTimeOffset SequenceEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string Uid(int appointmentId) => $"appointment-{appointmentId.ToString(CultureInfo.InvariantCulture)}@craneobarbero";

    public static int Sequence(Appointment appointment) =>
        (int)Math.Clamp((appointment.UpdatedAt - SequenceEpoch).TotalSeconds, 0, int.MaxValue);

    public byte[] Build(Appointment appointment)
    {
        var cancelled = appointment.Status == AppointmentStatus.Cancelled;
        var zone = clock.TimeZone.Id;
        var start = clock.ToBusinessTime(appointment.AppointmentDate);
        var end = clock.ToBusinessTime(appointment.EndDate);

        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Craneo Barbero//Turnos//ES",
            "CALSCALE:GREGORIAN",
            cancelled ? "METHOD:CANCEL" : "METHOD:PUBLISH",
            "BEGIN:VTIMEZONE",
            $"TZID:{zone}",
            "BEGIN:STANDARD",
            "DTSTART:19700101T000000",
            $"TZOFFSETFROM:{Offset(start.Offset)}",
            $"TZOFFSETTO:{Offset(start.Offset)}",
            "END:STANDARD",
            "END:VTIMEZONE",
            "BEGIN:VEVENT",
            $"UID:{Uid(appointment.Id)}",
            $"SEQUENCE:{Sequence(appointment).ToString(CultureInfo.InvariantCulture)}",
            $"DTSTAMP:{clock.UtcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}",
            $"DTSTART;TZID={zone}:{Local(start)}",
            $"DTEND;TZID={zone}:{Local(end)}",
            $"SUMMARY:{Escape(Title)}",
            $"DESCRIPTION:{Escape(Description(appointment))}",
        };

        if (appointment.Branch is { } branch)
        {
            lines.Add($"LOCATION:{Escape(branch.Address)}");
        }

        lines.Add(cancelled ? "STATUS:CANCELLED" : "STATUS:CONFIRMED");
        if (!cancelled)
        {
            lines.AddRange(
            [
                "BEGIN:VALARM",
                "ACTION:DISPLAY",
                $"DESCRIPTION:{Escape(Title)}",
                "TRIGGER:-PT1H",
                "END:VALARM",
            ]);
        }

        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            Fold(builder, line);
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public string GoogleCalendarUrl(Appointment appointment)
    {
        static string Utc(DateTimeOffset instant) =>
            instant.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        var query = new List<string>
        {
            "action=TEMPLATE",
            "text=" + Uri.EscapeDataString(Title),
            "dates=" + Utc(appointment.AppointmentDate) + "/" + Utc(appointment.EndDate),
            "details=" + Uri.EscapeDataString(Description(appointment)),
            "ctz=" + Uri.EscapeDataString(clock.TimeZone.Id),
        };
        if (appointment.Branch is { } branch)
        {
            query.Add("location=" + Uri.EscapeDataString(branch.Address));
        }

        return "https://calendar.google.com/calendar/render?" + string.Join('&', query);
    }

    public static string Description(Appointment appointment)
    {
        var parts = new List<string>();
        if (appointment.Barber is { } barber) parts.Add($"Barbero: {barber.Name}");
        if (appointment.Cut is { } cut) parts.Add($"Servicio: {cut.Name}");
        if (appointment.Branch is { } branch)
        {
            parts.Add($"Sucursal: {branch.Name}");
            parts.Add($"Dirección: {branch.Address}");
        }

        return string.Join('\n', parts);
    }

    private static string Local(DateTimeOffset instant) =>
        instant.DateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    private static string Offset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hhmm", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static void Fold(StringBuilder builder, string line)
    {
        var bytes = 0;
        var first = true;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            var limit = first ? 75 : 74;
            if (bytes + size > limit)
            {
                builder.Append("\r\n ");
                bytes = 0;
                first = false;
            }

            builder.Append(rune.ToString());
            bytes += size;
        }

        builder.Append("\r\n");
    }
}
