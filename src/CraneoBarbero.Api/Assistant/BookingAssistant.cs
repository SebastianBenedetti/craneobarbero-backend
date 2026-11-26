using System.Globalization;
using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Email;
using CraneoBarbero.Api.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Assistant;

public sealed class BookingAssistant(
    AppDbContext db,
    AvailabilityService availability,
    OpeningSchedule opening,
    BusinessClock clock,
    IOptions<BusinessSettings> business,
    IOptions<AssistantSettings> assistantSettings,
    AssistantConversationStore store,
    AssistantQuota quota,
    ITextInterpreter interpreter,
    OnlineBooking booking)
{
    private static readonly string[] ShortDays = ["dom", "lun", "mar", "mié", "jue", "vie", "sáb"];
    private static readonly string[] LongDays = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];

    private const string OnlyBookingMessage =
        "Solo puedo ayudarte a sacar turnos nuevos. Para ver, modificar o cancelar tus turnos entrá a «Mis turnos».";

    private const string NotUnderstoodMessage =
        "No llegué a entenderte. Elegí una de las opciones o escribilo de otra forma (por ejemplo: «el sábado a la mañana»).";

    public async Task<AssistantMessageResponse> HandleAsync(int userId, AssistantMessageRequest request, CancellationToken cancellationToken)
    {
        quota.EnsureMessageAllowed(userId);
        await booking.EnsureCanBookAsync(userId, cancellationToken);

        var catalog = await LoadCatalogAsync(cancellationToken);
        var choice = string.IsNullOrWhiteSpace(request.Choice) ? null : request.Choice.Trim();
        var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        var notes = new List<string>();

        var conversation = string.IsNullOrWhiteSpace(request.ConversationId) ? null : store.Find(request.ConversationId.Trim(), userId);
        if (conversation is null)
        {
            conversation = store.Create(userId);
            if (!string.IsNullOrWhiteSpace(request.ConversationId))
            {
                notes.Add("La conversación anterior venció, así que empezamos de nuevo.");
            }
        }
        else
        {
            CheckSelections(conversation, catalog, notes);
        }

        if (choice is not null)
        {
            await HandleChoiceAsync(conversation, catalog, choice, userId, notes, cancellationToken);
        }
        else if (message is not null)
        {
            await HandleTextAsync(conversation, catalog, message, userId, notes, cancellationToken);
        }

        var response = await RenderAsync(conversation, catalog, notes, userId, cancellationToken);
        store.Save(conversation);
        return response;
    }

    private async Task<AssistantCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        var branches = await db.Branches.AsNoTracking()
            .Where(b => b.IsActive)
            .OrderBy(b => b.Name)
            .Select(b => new CatalogBranch(b.Id, b.Name, b.Address))
            .ToListAsync(cancellationToken);
        var barbers = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Barber && u.IsActive && u.Branch != null && u.Branch.IsActive)
            .OrderBy(u => u.Name)
            .Select(u => new CatalogBarber(u.Id, u.Name, u.BranchId!.Value))
            .ToListAsync(cancellationToken);
        var cuts = await db.Cuts.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Price)
            .ThenBy(c => c.Name)
            .Select(c => new CatalogCut(c.Id, c.Name, c.Price, c.DurationMinutes))
            .ToListAsync(cancellationToken);
        return new AssistantCatalog(branches, barbers, cuts);
    }

    private static void CheckSelections(AssistantConversation conversation, AssistantCatalog catalog, List<string> notes)
    {
        if (conversation.Step == AssistantStep.Done)
        {
            return;
        }

        if (conversation.BranchId is { } branchId && catalog.Branches.All(b => b.Id != branchId))
        {
            conversation.Reset();
            notes.Add("La sucursal que habías elegido ya no está disponible.");
            return;
        }

        if (conversation.BarberId is { } barberId && catalog.Barbers.All(b => b.Id != barberId || b.BranchId != conversation.BranchId))
        {
            conversation.BarberId = null;
            conversation.ClearDay();
            notes.Add("El barbero que habías elegido ya no está disponible.");
        }

        if (conversation.CutId is { } cutId && catalog.Cuts.All(c => c.Id != cutId))
        {
            conversation.CutId = null;
            conversation.ClearDay();
            notes.Add("El servicio que habías elegido ya no está disponible.");
        }
    }

    private async Task HandleChoiceAsync(
        AssistantConversation conversation, AssistantCatalog catalog, string choice, int userId, List<string> notes,
        CancellationToken cancellationToken)
    {
        var separator = choice.IndexOf(':');
        var kind = separator < 0 ? choice : choice[..separator];
        var value = separator < 0 ? string.Empty : choice[(separator + 1)..];

        if (conversation.Step == AssistantStep.Done && kind != "restart")
        {
            return;
        }

        switch (kind)
        {
            case "start":
                if (conversation.Step == AssistantStep.Start)
                {
                    conversation.Step = AssistantStep.Branch;
                }

                break;
            case "restart":
                conversation.Reset();
                conversation.Step = AssistantStep.Branch;
                notes.Add("Empecemos de nuevo.");
                break;
            case "back":
                GoBack(conversation);
                break;
            case "branch" when TryId(value, out var branchId):
                if (!SelectBranch(conversation, catalog, branchId))
                {
                    notes.Add("Esa sucursal ya no está disponible.");
                }

                break;
            case "barber" when TryId(value, out var barberId):
                if (catalog.Barbers.Any(b => b.Id == barberId && b.BranchId != conversation.BranchId) ||
                    !SelectBarber(conversation, catalog, barberId))
                {
                    notes.Add("Ese barbero ya no está disponible.");
                }

                break;
            case "cut" when TryId(value, out var cutId):
                if (!SelectCut(conversation, catalog, cutId))
                {
                    notes.Add("Ese servicio ya no está disponible.");
                }

                break;
            case "day" when TextMatcher.ParseIsoDate(value) is { } date:
                if (!await SelectDayAsync(conversation, catalog, date, userId, cancellationToken))
                {
                    notes.Add("Ese día ya no tiene horarios libres. Elegí otro.");
                }

                break;
            case "days_more":
                conversation.DayPage++;
                break;
            case "days_less":
                conversation.DayPage = Math.Max(0, conversation.DayPage - 1);
                break;
            case "part":
                conversation.PreferredPart = value switch
                {
                    "morning" => DayPart.Morning,
                    "afternoon" => DayPart.Afternoon,
                    _ => null,
                };
                break;
            case "time" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start):
                if (!await SelectTimeAsync(conversation, catalog, start, userId, cancellationToken))
                {
                    notes.Add("Ese horario ya no está libre. Estos son los que siguen disponibles:");
                }

                break;
            case "confirm":
                await ConfirmAsync(conversation, catalog, userId, notes, cancellationToken);
                return;
            default:
                notes.Add("Esa opción ya no está disponible. Elegí una de estas:");
                break;
        }

        Advance(conversation);
    }

    private async Task HandleTextAsync(
        AssistantConversation conversation, AssistantCatalog catalog, string message, int userId, List<string> notes,
        CancellationToken cancellationToken)
    {
        if (message.Length > assistantSettings.Value.MaxMessageLength)
        {
            message = message[..assistantSettings.Value.MaxMessageLength];
        }

        var normalized = TextMatcher.Normalize(message);
        if (conversation.Step == AssistantStep.Done)
        {
            notes.Add("Tu turno ya quedó reservado. Si querés, podés sacar otro.");
            return;
        }

        if (TextMatcher.IsGreeting(normalized))
        {
            notes.Add(normalized.Contains("gracias", StringComparison.Ordinal)
                ? "¡De nada! Sigo acá para ayudarte con tu turno."
                : "¡Hola! Te ayudo a sacar un turno.");
            if (conversation.Step == AssistantStep.Start)
            {
                conversation.Step = AssistantStep.Branch;
            }

            return;
        }

        if (TextMatcher.IsRestart(normalized))
        {
            conversation.Reset();
            conversation.Step = AssistantStep.Branch;
            notes.Add("Empecemos de nuevo.");
            return;
        }

        if (conversation.Step == AssistantStep.Confirm && TextMatcher.IsYes(normalized))
        {
            await ConfirmAsync(conversation, catalog, userId, notes, cancellationToken);
            return;
        }

        if (conversation.Step == AssistantStep.Start)
        {
            conversation.Step = AssistantStep.Branch;
        }

        var today = clock.Today;
        var interpretation = TextMatcher.Interpret(message, catalog, today, conversation.BranchId);
        if (!interpretation.HasValues && !interpretation.OtherIntent)
        {
            interpretation = InterpretCurrentStep(conversation, catalog, normalized) ?? interpretation;
        }

        if (!interpretation.HasValues && !interpretation.OtherIntent && interpreter.IsConfigured && quota.TryUseAi(userId))
        {
            var interpreted = await interpreter.InterpretAsync(message, catalog, today, cancellationToken);
            if (interpreted is not null)
            {
                interpretation = Resolve(interpreted, catalog, conversation.BranchId);
            }
        }

        if (interpretation.OtherIntent)
        {
            notes.Add(OnlyBookingMessage);
            return;
        }

        if (!interpretation.HasValues)
        {
            notes.Add(NotUnderstoodMessage);
            return;
        }

        await ApplyAsync(conversation, catalog, interpretation, userId, notes, cancellationToken);
    }

    private AssistantInterpretation? InterpretCurrentStep(AssistantConversation conversation, AssistantCatalog catalog, string normalized)
    {
        if (normalized.Length < 3)
        {
            return null;
        }

        return conversation.Step switch
        {
            AssistantStep.Branch when TextMatcher.MatchBranch(normalized, catalog, allowPartial: true) is { } id => new AssistantInterpretation(BranchId: id),
            AssistantStep.Barber when TextMatcher.MatchBarber(normalized, catalog, conversation.BranchId, allowPartial: true) is { } id => new AssistantInterpretation(BarberId: id),
            AssistantStep.Cut when TextMatcher.MatchCut(normalized, catalog, allowPartial: true) is { } id => new AssistantInterpretation(CutId: id),
            _ => null,
        };
    }

    private static AssistantInterpretation Resolve(InterpretedText interpreted, AssistantCatalog catalog, int? currentBranchId)
    {
        if (string.Equals(interpreted.Intent, "other", StringComparison.OrdinalIgnoreCase))
        {
            return new AssistantInterpretation(OtherIntent: true);
        }

        var branchId = interpreted.Branch is { Length: > 0 } branch ? TextMatcher.MatchBranch(TextMatcher.Normalize(branch), catalog, true) : null;
        var barberId = interpreted.Barber is { Length: > 0 } barber
            ? catalog.Barbers.FirstOrDefault(b => TextMatcher.Normalize(b.Name) == TextMatcher.Normalize(barber))?.Id
              ?? TextMatcher.MatchBarber(TextMatcher.Normalize(barber), catalog, branchId ?? currentBranchId, true)
            : null;
        var cutId = interpreted.Service is { Length: > 0 } service
            ? catalog.Cuts.FirstOrDefault(c => TextMatcher.Normalize(c.Name) == TextMatcher.Normalize(service))?.Id
              ?? TextMatcher.MatchCut(TextMatcher.Normalize(service), catalog, true)
            : null;
        var part = interpreted.DayPart switch
        {
            "morning" => DayPart.Morning,
            "afternoon" => DayPart.Afternoon,
            _ => (DayPart?)null,
        };
        var time = TextMatcher.ParseTime(interpreted.Time);
        if (time is { } exact && part is null)
        {
            part = DayParts.Of(exact);
        }

        return new AssistantInterpretation(false, branchId, barberId, cutId, TextMatcher.ParseIsoDate(interpreted.Date), time, part);
    }

    private async Task ApplyAsync(
        AssistantConversation conversation, AssistantCatalog catalog, AssistantInterpretation interpretation, int userId,
        List<string> notes, CancellationToken cancellationToken)
    {
        var understood = new List<string>();
        if (interpretation.BarberId is { } barberId && SelectBarber(conversation, catalog, barberId))
        {
            var barber = catalog.Barbers.Single(b => b.Id == barberId);
            understood.Add($"{BranchName(catalog, barber.BranchId)}, con {barber.Name}");
        }
        else if (interpretation.BranchId is { } branchId && SelectBranch(conversation, catalog, branchId))
        {
            understood.Add(BranchName(catalog, branchId));
        }

        if (interpretation.CutId is { } cutId && SelectCut(conversation, catalog, cutId))
        {
            understood.Add(catalog.Cuts.Single(c => c.Id == cutId).Name);
        }

        if (interpretation.Part is { } part)
        {
            conversation.PreferredPart = part;
        }

        if (understood.Count > 0)
        {
            notes.Add("Entendí: " + string.Join(" · ", understood) + ".");
        }

        Advance(conversation);

        if (interpretation.Date is { } date)
        {
            if (date < clock.Today || date > LastBookableDate())
            {
                notes.Add($"Solo puedo reservar desde hoy hasta el {Short(LastBookableDate())}.");
            }
            else if (conversation.BarberId is null || conversation.CutId is null)
            {
                notes.Add($"Anoté el {Long(date)}: primero elegí lo que falta.");
                conversation.PendingDate = date;
            }
            else if (await SelectDayAsync(conversation, catalog, date, userId, cancellationToken))
            {
                notes.Add($"Día: {Long(date)}.");
            }
            else
            {
                notes.Add($"El {Long(date)} no hay horarios libres. Elegí otro día.");
            }
        }

        Advance(conversation);

        if (interpretation.Time is { } time && conversation.Date is { } chosenDay)
        {
            var start = clock.ToUtc(chosenDay, time);
            if (await SelectTimeAsync(conversation, catalog, start, userId, cancellationToken))
            {
                notes.Add($"Horario: {time:HH:mm}.");
            }
            else
            {
                notes.Add($"A las {time:HH:mm} no hay lugar. Te muestro los horarios libres.");
            }
        }

        Advance(conversation);
    }

    private static bool SelectBranch(AssistantConversation conversation, AssistantCatalog catalog, int branchId)
    {
        if (catalog.Branches.All(b => b.Id != branchId))
        {
            return false;
        }

        if (conversation.BranchId != branchId)
        {
            conversation.BranchId = branchId;
            conversation.BarberId = null;
            conversation.ClearDay();
        }

        return true;
    }

    private static bool SelectBarber(AssistantConversation conversation, AssistantCatalog catalog, int barberId)
    {
        var barber = catalog.Barbers.FirstOrDefault(b => b.Id == barberId);
        if (barber is null)
        {
            return false;
        }

        if (conversation.BarberId != barberId)
        {
            conversation.BranchId = barber.BranchId;
            conversation.BarberId = barberId;
            conversation.ClearDay();
        }

        return true;
    }

    private static bool SelectCut(AssistantConversation conversation, AssistantCatalog catalog, int cutId)
    {
        if (catalog.Cuts.All(c => c.Id != cutId))
        {
            return false;
        }

        if (conversation.CutId != cutId)
        {
            conversation.CutId = cutId;
            conversation.StartAt = null;
            if (conversation.Date is not null)
            {
                conversation.Date = null;
                conversation.DayPage = 0;
            }
        }

        return true;
    }

    private async Task<bool> SelectDayAsync(
        AssistantConversation conversation, AssistantCatalog catalog, DateOnly date, int userId, CancellationToken cancellationToken)
    {
        if (conversation.BarberId is not { } barberId || Cut(catalog, conversation) is not { } cut)
        {
            return false;
        }

        var starts = await availability.GetAvailableStartsAsync(Query(barberId, cut, date, userId), cancellationToken);
        if (starts.Count == 0)
        {
            return false;
        }

        if (conversation.Date != date)
        {
            conversation.Date = date;
            conversation.StartAt = null;
        }

        return true;
    }

    private async Task<bool> SelectTimeAsync(
        AssistantConversation conversation, AssistantCatalog catalog, DateTimeOffset start, int userId, CancellationToken cancellationToken)
    {
        if (conversation.BarberId is not { } barberId || Cut(catalog, conversation) is not { } cut || conversation.Date is not { } date)
        {
            return false;
        }

        var starts = await availability.GetAvailableStartsAsync(Query(barberId, cut, date, userId), cancellationToken);
        var match = starts.FirstOrDefault(s => s == start.ToUniversalTime());
        if (match == default)
        {
            conversation.StartAt = null;
            return false;
        }

        conversation.StartAt = match;
        return true;
    }

    private async Task ConfirmAsync(
        AssistantConversation conversation, AssistantCatalog catalog, int userId, List<string> notes, CancellationToken cancellationToken)
    {
        if (conversation.Step != AssistantStep.Confirm || conversation.BarberId is not { } barberId ||
            conversation.CutId is not { } cutId || conversation.StartAt is not { } start)
        {
            Advance(conversation);
            return;
        }

        try
        {
            var result = await booking.BookAsync(userId, barberId, cutId, start, conversation.BranchId, cancellationToken);
            conversation.AppointmentId = result.Appointment.Id;
            conversation.CheckoutUrl = result.CheckoutUrl;
            conversation.Step = AssistantStep.Done;
        }
        catch (AppException exception) when (exception is ConflictException or BadRequestException or NotFoundException)
        {
            conversation.StartAt = null;
            notes.Add(exception.Code == ErrorCodes.ClientOverlap
                ? "Ya tenés otro turno en ese horario. Elegí otro:"
                : exception.Code == ErrorCodes.SlotUnavailable
                    ? "Ese horario se acaba de ocupar. Estos son los que siguen libres:"
                    : exception.Message);
            CheckSelections(conversation, await LoadCatalogAsync(cancellationToken), notes);
            Advance(conversation);
        }
    }

    private static void GoBack(AssistantConversation conversation)
    {
        switch (conversation.Step)
        {
            case AssistantStep.Barber:
                conversation.BranchId = null;
                break;
            case AssistantStep.Cut:
                conversation.BarberId = null;
                conversation.ClearDay();
                break;
            case AssistantStep.Day:
                conversation.CutId = null;
                conversation.ClearDay();
                break;
            case AssistantStep.Time:
                conversation.Date = null;
                conversation.StartAt = null;
                break;
            case AssistantStep.Confirm:
                conversation.StartAt = null;
                break;
        }
    }

    private static void Advance(AssistantConversation conversation)
    {
        if (conversation.Step == AssistantStep.Done ||
            (conversation.Step == AssistantStep.Start && conversation.BranchId is null))
        {
            return;
        }

        conversation.Step = conversation.BranchId is null ? AssistantStep.Branch
            : conversation.BarberId is null ? AssistantStep.Barber
            : conversation.CutId is null ? AssistantStep.Cut
            : conversation.Date is null ? AssistantStep.Day
            : conversation.StartAt is null ? AssistantStep.Time
            : AssistantStep.Confirm;
    }

    private async Task<AssistantMessageResponse> RenderAsync(
        AssistantConversation conversation, AssistantCatalog catalog, List<string> notes, int userId, CancellationToken cancellationToken)
    {
        if (conversation.Step == AssistantStep.Day && conversation.PendingDate is { } pending)
        {
            conversation.PendingDate = null;
            if (await SelectDayAsync(conversation, catalog, pending, userId, cancellationToken))
            {
                notes.Add($"Día: {Long(pending)}.");
            }
            else
            {
                notes.Add($"El {Long(pending)} no hay horarios libres. Elegí otro día.");
            }

            Advance(conversation);
        }

        var options = new List<AssistantOption>();
        string prompt;
        AssistantSummary? summary = null;

        switch (conversation.Step)
        {
            case AssistantStep.Start:
                prompt = "¡Hola! Soy la asistente de Cráneo Barbero. Te ayudo a sacar un turno: elegí las opciones o escribime, por ejemplo, «el sábado a la mañana».";
                options.Add(new AssistantOption("Quiero sacar un turno", "start"));
                break;

            case AssistantStep.Branch:
                if (catalog.Branches.Count == 0)
                {
                    prompt = "Ahora no hay sucursales disponibles para reservar. Probá más tarde.";
                    break;
                }

                prompt = "¿En qué sucursal querés atenderte?";
                options.AddRange(catalog.Branches.Select(b => new AssistantOption(b.Name, $"branch:{b.Id}")));
                break;

            case AssistantStep.Barber:
                var barbers = catalog.Barbers.Where(b => b.BranchId == conversation.BranchId).ToList();
                if (barbers.Count == 0)
                {
                    notes.Add($"{BranchName(catalog, conversation.BranchId)} no tiene barberos disponibles ahora.");
                    conversation.BranchId = null;
                    conversation.Step = AssistantStep.Branch;
                    return await RenderAsync(conversation, catalog, notes, userId, cancellationToken);
                }

                prompt = $"¿Con qué barbero querés atenderte en {BranchName(catalog, conversation.BranchId)}?";
                options.AddRange(barbers.Select(b => new AssistantOption(b.Name, $"barber:{b.Id}")));
                options.Add(new AssistantOption("Volver", "back"));
                break;

            case AssistantStep.Cut:
                prompt = "¿Qué servicio querés?";
                options.AddRange(catalog.Cuts.Select(c =>
                    new AssistantOption($"{c.Name} · {AppointmentEmailTemplates.FormatMoney(c.Price)} · {c.DurationMinutes} min", $"cut:{c.Id}")));
                options.Add(new AssistantOption("Volver", "back"));
                break;

            case AssistantStep.Day:
            {
                var cut = Cut(catalog, conversation)!;
                var barber = catalog.Barbers.Single(b => b.Id == conversation.BarberId);
                var counts = await availability.CountByDayAsync(
                    Query(barber.Id, cut, clock.Today, userId), clock.Today, LastBookableDate(), cancellationToken);
                var days = counts.Where(d => d.Available > 0).Select(d => d.Date).ToList();
                if (days.Count == 0)
                {
                    notes.Add($"{barber.Name} no tiene horarios libres en los próximos {business.Value.BookingWindowDays} días para ese servicio. Elegí otro barbero.");
                    conversation.BarberId = null;
                    conversation.ClearDay();
                    conversation.Step = AssistantStep.Barber;
                    return await RenderAsync(conversation, catalog, notes, userId, cancellationToken);
                }

                var pageSize = Math.Max(1, assistantSettings.Value.DaysPerPage);
                var pages = (days.Count + pageSize - 1) / pageSize;
                conversation.DayPage = Math.Clamp(conversation.DayPage, 0, pages - 1);
                prompt = $"¿Qué día? Estos son los días con horarios libres de {barber.Name}:";
                options.AddRange(days.Skip(conversation.DayPage * pageSize).Take(pageSize)
                    .Select(d => new AssistantOption(Short(d), $"day:{d:yyyy-MM-dd}")));
                if (conversation.DayPage > 0)
                {
                    options.Add(new AssistantOption("Días anteriores", "days_less"));
                }

                if (conversation.DayPage < pages - 1)
                {
                    options.Add(new AssistantOption("Ver más días", "days_more"));
                }

                options.Add(new AssistantOption("Volver", "back"));
                break;
            }

            case AssistantStep.Time:
            {
                var cut = Cut(catalog, conversation)!;
                var date = conversation.Date!.Value;
                var starts = await availability.GetAvailableStartsAsync(Query(conversation.BarberId!.Value, cut, date, userId), cancellationToken);
                if (starts.Count == 0)
                {
                    notes.Add($"El {Long(date)} ya no tiene horarios libres.");
                    conversation.ClearDay();
                    conversation.Step = AssistantStep.Day;
                    return await RenderAsync(conversation, catalog, notes, userId, cancellationToken);
                }

                var filtered = conversation.PreferredPart switch
                {
                    DayPart.Morning => starts.Where(s => DayParts.Of(opening.TimeOf(s)) == DayPart.Morning).ToList(),
                    DayPart.Afternoon => starts.Where(s => DayParts.Of(opening.TimeOf(s)) == DayPart.Afternoon).ToList(),
                    _ => starts.ToList(),
                };
                var partLabel = conversation.PreferredPart == DayPart.Morning ? " de la mañana" : " de la tarde";
                if (filtered.Count == 0)
                {
                    notes.Add($"El {Long(date)} no quedan horarios{partLabel}. Te muestro todos los libres.");
                    conversation.PreferredPart = null;
                    filtered = starts.ToList();
                }

                prompt = conversation.PreferredPart is null
                    ? $"¿A qué hora el {Long(date)}? Estos son los horarios libres:"
                    : $"¿A qué hora el {Long(date)}? Estos son los horarios libres{partLabel}:";
                options.AddRange(filtered.Select(s => new AssistantOption(opening.TimeOf(s).ToString("HH:mm", CultureInfo.InvariantCulture),
                    "time:" + s.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))));
                if (conversation.PreferredPart is not null)
                {
                    options.Add(new AssistantOption("Ver todos los horarios", "part:all"));
                }

                options.Add(new AssistantOption("Cambiar el día", "back"));
                break;
            }

            case AssistantStep.Confirm:
                summary = Summary(conversation, catalog);
                prompt = "Revisá el resumen. Si está todo bien, confirmá y te paso el enlace para pagar el total con Mercado Pago.";
                options.Add(new AssistantOption("Confirmar y pagar", "confirm"));
                options.Add(new AssistantOption("Cambiar el horario", "back"));
                options.Add(new AssistantOption("Empezar de nuevo", "restart"));
                break;

            default:
                prompt = conversation.CheckoutUrl is null
                    ? "Tu turno quedó reservado, pero no pude generar el enlace de pago. Entrá a «Mis turnos» y tocá «Pagar»: tenés 30 minutos."
                    : $"¡Listo! Tu turno quedó reservado. Tenés {business.Value.PaymentExpirationMinutes} minutos para pagarlo; cuando se acredite el pago te llega el mail de confirmación.";
                options.Add(new AssistantOption("Sacar otro turno", "restart"));
                break;
        }

        var reply = string.Join("\n", notes.Append(prompt));
        return new AssistantMessageResponse(
            conversation.Id,
            reply,
            options,
            conversation.Step,
            summary,
            conversation.Step == AssistantStep.Done ? conversation.CheckoutUrl : null,
            conversation.Step == AssistantStep.Done ? conversation.AppointmentId : null);
    }

    private AssistantSummary Summary(AssistantConversation conversation, AssistantCatalog catalog)
    {
        var branch = catalog.Branches.Single(b => b.Id == conversation.BranchId);
        var barber = catalog.Barbers.Single(b => b.Id == conversation.BarberId);
        var cut = Cut(catalog, conversation)!;
        var start = conversation.StartAt!.Value;
        var date = opening.DateOf(start);
        return new AssistantSummary(
            branch.Name,
            branch.Address,
            barber.Name,
            cut.Name,
            cut.DurationMinutes,
            date,
            Long(date),
            opening.TimeOf(start).ToString("HH:mm", CultureInfo.InvariantCulture),
            start,
            cut.Price);
    }

    private static CatalogCut? Cut(AssistantCatalog catalog, AssistantConversation conversation) =>
        catalog.Cuts.FirstOrDefault(c => c.Id == conversation.CutId);

    private static AvailabilityQuery Query(int barberId, CatalogCut cut, DateOnly date, int userId) =>
        new(barberId, cut.DurationMinutes, date, ApplyBookingWindow: true, ClientId: userId);

    private DateOnly LastBookableDate() => opening.DateOf(clock.UtcNow.AddDays(business.Value.BookingWindowDays));

    private static string BranchName(AssistantCatalog catalog, int? branchId) =>
        catalog.Branches.FirstOrDefault(b => b.Id == branchId)?.Name ?? "la sucursal";

    private static string Short(DateOnly date) => $"{ShortDays[(int)date.DayOfWeek]} {date:dd/MM}";

    private static string Long(DateOnly date) => $"{LongDays[(int)date.DayOfWeek]} {date:dd/MM}";

    private static bool TryId(string value, out int id) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}
