using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Payments;
using CraneoBarbero.Api.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("orders")]
public sealed class OrdersController(
    AppDbContext db,
    OrderManagement orders,
    OrderPayments payments,
    BusinessClock clock) : ControllerBase
{
    private const string NotFoundMessage = "No se encontró el pedido.";

    [Authorize(Policy = Policies.Client)]
    [HttpPost]
    public async Task<ActionResult<CreatedOrderResponse>> Create(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var clientId = User.GetRequiredUserId();
        var verified = await db.Users.AsNoTracking().Where(u => u.Id == clientId).Select(u => u.EmailVerified).SingleAsync(cancellationToken);
        if (!verified)
        {
            throw new ForbiddenException("Verificá tu email para poder comprar.", ErrorCodes.EmailNotVerified);
        }

        var order = await orders.CreateAsync(clientId, request, cancellationToken);
        var checkoutUrl = await payments.TryStartCheckoutAsync(order, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, new CreatedOrderResponse(await LoadAsync(order.Id, false, cancellationToken), checkoutUrl));
    }

    [Authorize(Policy = Policies.Client)]
    [HttpPost("{id}/pay")]
    public async Task<OrderCheckoutResponse> Pay(int id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
                    ?? throw new NotFoundException(NotFoundMessage);
        if (order.ClientId != User.GetRequiredUserId())
        {
            throw new ForbiddenException();
        }

        var url = await payments.StartCheckoutAsync(order, cancellationToken);
        return new OrderCheckoutResponse(order.Id, url, order.PaymentExpiresAt);
    }

    [Authorize(Policy = Policies.Client)]
    [HttpGet("mine")]
    public async Task<PagedResponse<OrderResponse>> Mine([FromQuery] MyOrdersQuery query, CancellationToken cancellationToken)
    {
        var clientId = User.GetRequiredUserId();
        var mine = db.Orders.AsNoTracking().Where(o => o.ClientId == clientId && o.Status != OrderStatus.Expired);
        return await PageAsync(mine, query.Page, query.PageSize, false, cancellationToken);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet]
    public async Task<PagedResponse<OrderResponse>> List([FromQuery] OrderListQuery query, CancellationToken cancellationToken)
    {
        var all = db.Orders.AsNoTracking();
        if (query.Status is { } status)
        {
            all = all.Where(o => o.Status == status);
        }

        if (query.BranchId is { } branchId)
        {
            all = all.Where(o => o.BranchId == branchId);
        }

        if (query.ClientId is { } clientId)
        {
            all = all.Where(o => o.ClientId == clientId);
        }

        if (query.From is { } from)
        {
            var start = clock.ToUtc(from, TimeOnly.MinValue);
            all = all.Where(o => o.CreatedAt >= start);
        }

        if (query.To is { } to)
        {
            var end = clock.ToUtc(to.AddDays(1), TimeOnly.MinValue);
            all = all.Where(o => o.CreatedAt < end);
        }

        return await PageAsync(all, query.Page, query.PageSize, true, cancellationToken);
    }

    [Authorize(Policy = Policies.ClientOrAdmin)]
    [HttpGet("{id}")]
    public async Task<OrderResponse> Get(int id, CancellationToken cancellationToken)
    {
        var isAdmin = User.IsInRole(Roles.Admin);
        var order = await WithDetails(db.Orders.AsNoTracking()).FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
                    ?? throw new NotFoundException(NotFoundMessage);
        if (!isAdmin && order.ClientId != User.GetRequiredUserId())
        {
            throw new ForbiddenException();
        }

        return OrderResponse.From(order, isAdmin);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id}/status")]
    public async Task<OrderResponse> UpdateStatus(int id, UpdateOrderStatusRequest request, CancellationToken cancellationToken)
    {
        await orders.UpdateStatusAsync(id, request.Status!.Value, cancellationToken);
        return await LoadAsync(id, true, cancellationToken);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id}/cancel")]
    public async Task<OrderResponse> Cancel(int id, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        await orders.CancelAsync(id, User.GetRequiredUserId(), request, cancellationToken);
        return await LoadAsync(id, true, cancellationToken);
    }

    private static IQueryable<Order> WithDetails(IQueryable<Order> source) =>
        source.Include(o => o.Client).Include(o => o.Branch).Include(o => o.Items).ThenInclude(i => i.Product);

    private async Task<OrderResponse> LoadAsync(int id, bool forAdmin, CancellationToken cancellationToken) =>
        OrderResponse.From(await WithDetails(db.Orders.AsNoTracking()).SingleAsync(o => o.Id == id, cancellationToken), forAdmin);

    private static async Task<PagedResponse<OrderResponse>> PageAsync(
        IQueryable<Order> source, int? page, int? pageSize, bool forAdmin, CancellationToken cancellationToken)
    {
        var number = page ?? 1;
        var size = pageSize ?? AppointmentLimits.DefaultPageSize;
        var total = await source.CountAsync(cancellationToken);
        var rows = await WithDetails(source)
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Skip((number - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        return new PagedResponse<OrderResponse>(rows.Select(o => OrderResponse.From(o, forAdmin)).ToList(), number, size, total);
    }
}
