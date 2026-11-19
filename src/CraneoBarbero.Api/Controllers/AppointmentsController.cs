using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Schedule;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("appointments")]
public sealed class AppointmentsController(
    AppDbContext db,
    AppointmentBooking booking,
    AppointmentChanges changes,
    Payments.AppointmentPayments payments,
    OpeningSchedule opening,
    IAuthorizationService authorization) : ControllerBase
{
    private const string NotFoundMessage = "No se encontró el turno.";

    [Authorize(Policy = Policies.Client)]
    [HttpPost]
    public async Task<ActionResult<CreatedAppointmentResponse>> Create(CreateAppointmentRequest request, CancellationToken cancellationToken)
    {
        var clientId = User.GetRequiredUserId();
        var client = await db.Users.AsNoTracking().SingleAsync(u => u.Id == clientId, cancellationToken);
        if (!client.EmailVerified)
        {
            throw new ForbiddenException("Verificá tu email para poder reservar turnos.", ErrorCodes.EmailNotVerified);
        }

        var appointment = await booking.BookAsync(new BookingRequest(
            request.BarberId!.Value,
            request.CutId!.Value,
            request.AppointmentDate!.Value,
            request.BranchId,
            clientId,
            null,
            UserRole.Client,
            clientId,
            PaymentMethod.MercadoPago,
            Online: true), cancellationToken);

        var checkoutUrl = await payments.TryStartCheckoutAsync(appointment, cancellationToken);
        var response = new CreatedAppointmentResponse(await LoadAsync(appointment.Id, cancellationToken), checkoutUrl);
        return CreatedAtAction(nameof(Get), new { id = appointment.Id }, response);
    }

    [Authorize(Policy = Policies.BarberOrAdmin)]
    [HttpPost("manual")]
    public async Task<ActionResult<AppointmentResponse>> CreateManual(
        CreateManualAppointmentRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var isAdmin = User.IsInRole(Roles.Admin);

        int barberId;
        if (isAdmin)
        {
            barberId = request.BarberId ?? throw new RequestValidationException(
                new Dictionary<string, string[]> { ["barberId"] = ["Elegí un barbero."] });
        }
        else
        {
            if (request.BarberId is not null && request.BarberId != userId)
            {
                throw new ForbiddenException("Solo podés cargar turnos para vos.");
            }

            barberId = userId;
        }

        if (request.ClientId is not null)
        {
            var clientOk = await db.Users.AsNoTracking()
                .AnyAsync(u => u.Id == request.ClientId && u.Role == UserRole.Client && u.IsActive, cancellationToken);
            if (!clientOk)
            {
                throw new BadRequestException("El cliente elegido no existe o está desactivado.", ErrorCodes.ClientUnavailable);
            }
        }

        var appointment = await booking.BookAsync(new BookingRequest(
            barberId,
            request.CutId!.Value,
            request.AppointmentDate!.Value,
            null,
            request.ClientId,
            AppointmentText.CleanName(request.ClientName),
            isAdmin ? UserRole.Admin : UserRole.Barber,
            userId,
            request.PaymentMethod!.Value,
            Online: false), cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = appointment.Id }, await LoadAsync(appointment.Id, cancellationToken));
    }

    [Authorize(Policy = Policies.BarberOrAdmin)]
    [HttpGet]
    public async Task<PagedResponse<AppointmentResponse>> List([FromQuery] AppointmentListQuery query, CancellationToken cancellationToken)
    {
        var appointments = Full();
        if (User.IsInRole(Roles.Barber))
        {
            var barberId = User.GetRequiredUserId();
            appointments = appointments.Where(a => a.BarberId == barberId);
        }
        else if (query.BarberId is not null)
        {
            appointments = appointments.Where(a => a.BarberId == query.BarberId);
        }

        if (query.BranchId is not null) appointments = appointments.Where(a => a.BranchId == query.BranchId);
        if (query.Status is not null) appointments = appointments.Where(a => a.Status == query.Status);
        if (query.From is not null)
        {
            var from = opening.DayRange(query.From.Value).Start;
            appointments = appointments.Where(a => a.AppointmentDate >= from);
        }

        if (query.To is not null)
        {
            var to = opening.DayRange(query.To.Value).End;
            appointments = appointments.Where(a => a.AppointmentDate < to);
        }

        return await PageAsync(appointments.OrderByDescending(a => a.AppointmentDate).ThenByDescending(a => a.Id), query, cancellationToken);
    }

    [Authorize(Policy = Policies.Client)]
    [HttpGet("my")]
    public async Task<PagedResponse<AppointmentResponse>> Mine([FromQuery] AppointmentListQuery query, CancellationToken cancellationToken)
    {
        var clientId = User.GetRequiredUserId();
        var appointments = Full().Where(a => a.ClientId == clientId && a.Status != AppointmentStatus.Expired);
        if (query.Status is not null) appointments = appointments.Where(a => a.Status == query.Status);
        return await PageAsync(appointments.OrderByDescending(a => a.AppointmentDate).ThenByDescending(a => a.Id), query, cancellationToken);
    }

    [HttpGet("{id}")]
    public async Task<AppointmentResponse> Get(int id, CancellationToken cancellationToken)
    {
        var appointment = await Full().FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                          ?? throw new NotFoundException(NotFoundMessage);

        await authorization.EnsureAuthorizedAsync(User, new OwnedResource(appointment.ClientId, appointment.BarberId));
        return AppointmentResponse.From(appointment);
    }

    [HttpGet("{id}/cancellation-preview")]
    public async Task<CancellationPreview> CancellationPreview(int id, CancellationToken cancellationToken)
    {
        var actor = CurrentActor();
        var appointment = await changes.FindForAsync(id, actor, cancellationToken);
        return await changes.PreviewCancellationAsync(appointment, actor, cancellationToken);
    }

    [Authorize(Policy = Policies.Client)]
    [HttpPost("{id}/pay")]
    public async Task<Payments.CheckoutResponse> Pay(int id, CancellationToken cancellationToken)
    {
        var actor = CurrentActor();
        var appointment = await changes.FindForAsync(id, actor, cancellationToken);
        var url = await payments.StartCheckoutAsync(appointment, cancellationToken);
        return new Payments.CheckoutResponse(appointment.Id, url, appointment.PaymentExpiresAt);
    }

    [HttpPost("{id}/cancel")]
    public async Task<AppointmentResponse> Cancel(
        int id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CancelAppointmentRequest? request,
        CancellationToken cancellationToken)
    {
        var actor = CurrentActor();
        var appointment = await changes.FindForAsync(id, actor, cancellationToken);
        await changes.CancelAsync(appointment, actor, request?.Reason, cancellationToken);
        return await LoadAsync(id, cancellationToken);
    }

    [HttpPut("{id}/reschedule")]
    public async Task<AppointmentResponse> Reschedule(int id, RescheduleAppointmentRequest request, CancellationToken cancellationToken)
    {
        var actor = CurrentActor();
        var appointment = await changes.FindForAsync(id, actor, cancellationToken);
        await changes.RescheduleAsync(appointment, actor, request, cancellationToken);
        return await LoadAsync(id, cancellationToken);
    }

    private Actor CurrentActor()
    {
        var role = User.IsInRole(Roles.Admin) ? UserRole.Admin
            : User.IsInRole(Roles.Barber) ? UserRole.Barber
            : UserRole.Client;
        return new Actor(User.GetRequiredUserId(), role);
    }

    private IQueryable<Appointment> Full() => db.Appointments.AsNoTracking()
        .Include(a => a.Client)
        .Include(a => a.Barber)
        .Include(a => a.Cut)
        .Include(a => a.Branch);

    private async Task<AppointmentResponse> LoadAsync(int id, CancellationToken cancellationToken) =>
        AppointmentResponse.From(await Full().SingleAsync(a => a.Id == id, cancellationToken));

    private static async Task<PagedResponse<AppointmentResponse>> PageAsync(
        IQueryable<Appointment> appointments, AppointmentListQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page ?? 1;
        var size = query.PageSize ?? AppointmentLimits.DefaultPageSize;
        var total = await appointments.CountAsync(cancellationToken);
        var items = await appointments.Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new PagedResponse<AppointmentResponse>(items.Select(AppointmentResponse.From).ToList(), page, size, total);
    }
}

[ApiController]
[Route("clients")]
public sealed class ClientsController(AppDbContext db) : ControllerBase
{
    [Authorize(Policy = Policies.BarberOrAdmin)]
    [HttpGet("search")]
    public async Task<IReadOnlyList<ClientSearchResult>> Search([FromQuery] string? q, CancellationToken cancellationToken)
    {
        var term = q?.Trim() ?? string.Empty;
        if (term.Length < AppointmentLimits.ClientSearchMinLength)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["q"] = [$"Ingresá al menos {AppointmentLimits.ClientSearchMinLength} letras."],
            });
        }

        if (term.Length > 100)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["q"] = ["La búsqueda es demasiado larga."] });
        }

        var found = await Matching(db.Users.AsNoTracking(), term, db.Database.IsNpgsql())
            .OrderBy(u => u.Name)
            .Take(AppointmentLimits.ClientSearchMaxResults)
            .Select(u => new { u.Id, u.Name, u.Email })
            .ToListAsync(cancellationToken);
        return found.Select(u => new ClientSearchResult(u.Id, u.Name, EmailMask.Mask(u.Email))).ToList();
    }

    public static IQueryable<User> Matching(IQueryable<User> users, string term, bool npgsql)
    {
        var lower = term.ToLowerInvariant();
        var pattern = $"%{EscapeLike(lower)}%";
        var clients = users.Where(u => u.Role == UserRole.Client && u.IsActive);
        return npgsql
            ? clients.Where(u => EF.Functions.ILike(u.Name, pattern, "\\") || EF.Functions.ILike(u.Email, pattern, "\\"))
            : clients.Where(u => u.Name.ToLower().Contains(lower) || u.Email.Contains(lower));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
