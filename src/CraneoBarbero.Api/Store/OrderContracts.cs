using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using FluentValidation;

namespace CraneoBarbero.Api.Store;

public sealed record OrderItemRequest(int? ProductId, int? Quantity);

public sealed record CreateOrderRequest(int? BranchId, IReadOnlyList<OrderItemRequest>? Items);

public sealed record UpdateOrderStatusRequest(OrderStatus? Status);

public sealed record CancelOrderRequest(bool? Refund, string? Reason);

public sealed record OrderListQuery(
    OrderStatus? Status,
    int? BranchId,
    int? ClientId,
    DateOnly? From,
    DateOnly? To,
    int? Page,
    int? PageSize);

public sealed record MyOrdersQuery(int? Page, int? PageSize);

public sealed record OrderItemResponse(
    int ProductId,
    string ProductName,
    string? ImageUrl,
    int Quantity,
    decimal UnitPrice,
    decimal Subtotal,
    decimal? UnitCost);

public sealed record OrderClientResponse(int Id, string Name, string Email, string? Phone);

public sealed record OrderBranchResponse(int Id, string Name, string Address, string? Phone);

public sealed record OrderResponse(
    int Id,
    OrderStatus Status,
    decimal Total,
    RefundStatus RefundStatus,
    DateTimeOffset? PaymentExpiresAt,
    string? CancellationReason,
    OrderClientResponse? Client,
    OrderBranchResponse Branch,
    IReadOnlyList<OrderItemResponse> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static OrderResponse From(Order order, bool forAdmin) => new(
        order.Id,
        order.Status,
        order.Total,
        order.RefundStatus,
        order.PaymentExpiresAt,
        order.CancellationReason,
        order.Client is null ? null : new OrderClientResponse(order.Client.Id, order.Client.Name, order.Client.Email, order.Client.Phone),
        new OrderBranchResponse(order.Branch!.Id, order.Branch.Name, order.Branch.Address, order.Branch.Phone),
        order.Items
            .OrderBy(i => i.Id)
            .Select(i => new OrderItemResponse(
                i.ProductId,
                i.Product!.Name,
                i.Product.ImageUrl,
                i.Quantity,
                i.UnitPrice,
                i.Quantity * i.UnitPrice,
                forAdmin ? i.UnitCost : null))
            .ToList(),
        order.CreatedAt,
        order.UpdatedAt);
}

public sealed record CreatedOrderResponse(OrderResponse Order, string? CheckoutUrl);

public sealed record OrderCheckoutResponse(int OrderId, string CheckoutUrl, DateTimeOffset? PaymentExpiresAt);

public static class OrderLimits
{
    public const int MaxItems = 30;
    public const int MaxQuantityPerItem = 100;
    public const int MaxReasonLength = 300;
    public const string OutOfStockReason = "out_of_stock";
}

public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderRequestValidator()
    {
        RuleFor(r => r.BranchId).NotNull().WithMessage("Elegí la sucursal donde vas a retirar el pedido.")
            .GreaterThan(0).WithMessage("Elegí la sucursal donde vas a retirar el pedido.");
        RuleFor(r => r.Items).NotEmpty().WithMessage("El carrito está vacío.")
            .Must(items => items is null || items.Count <= OrderLimits.MaxItems)
            .WithMessage($"Un pedido admite hasta {OrderLimits.MaxItems} productos distintos.")
            .Must(items => items is null || items.Where(i => i?.ProductId is not null).GroupBy(i => i.ProductId).All(g => g.Count() == 1))
            .WithMessage("Cada producto puede aparecer una sola vez en el pedido.");
        RuleForEach(r => r.Items).NotNull().WithMessage("El producto no es válido.").ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotNull().WithMessage("Elegí el producto.")
                .GreaterThan(0).WithMessage("Elegí el producto.");
            item.RuleFor(i => i.Quantity).NotNull().WithMessage("Ingresá la cantidad.")
                .InclusiveBetween(1, OrderLimits.MaxQuantityPerItem)
                .WithMessage($"La cantidad debe estar entre 1 y {OrderLimits.MaxQuantityPerItem}.");
        });
    }
}

public sealed class UpdateOrderStatusRequestValidator : AbstractValidator<UpdateOrderStatusRequest>
{
    public UpdateOrderStatusRequestValidator()
    {
        RuleFor(r => r.Status).NotNull().WithMessage("Elegí el nuevo estado.")
            .Must(status => status is null or OrderStatus.Ready or OrderStatus.Delivered)
            .WithMessage("El estado solo puede pasar a listo para retirar o entregado.");
    }
}

public sealed class CancelOrderRequestValidator : AbstractValidator<CancelOrderRequest>
{
    public CancelOrderRequestValidator()
    {
        RuleFor(r => r.Refund).NotNull().WithMessage("Indicá si el pedido se cancela con reembolso o sin reembolso.");
        RuleFor(r => r.Reason).MaximumLength(OrderLimits.MaxReasonLength)
            .WithMessage($"El motivo admite hasta {OrderLimits.MaxReasonLength} caracteres.");
    }
}

public sealed class OrderListQueryValidator : AbstractValidator<OrderListQuery>
{
    public OrderListQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThan(0).When(q => q.Page is not null).WithMessage("La página debe ser mayor a 0.");
        RuleFor(q => q.PageSize).InclusiveBetween(1, AppointmentLimits.MaxPageSize).When(q => q.PageSize is not null)
            .WithMessage($"El tamaño de página debe estar entre 1 y {AppointmentLimits.MaxPageSize}.");
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From).When(q => q.From is not null && q.To is not null)
            .WithMessage("La fecha final no puede ser anterior a la inicial.");
    }
}

public sealed class MyOrdersQueryValidator : AbstractValidator<MyOrdersQuery>
{
    public MyOrdersQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThan(0).When(q => q.Page is not null).WithMessage("La página debe ser mayor a 0.");
        RuleFor(q => q.PageSize).InclusiveBetween(1, AppointmentLimits.MaxPageSize).When(q => q.PageSize is not null)
            .WithMessage($"El tamaño de página debe estar entre 1 y {AppointmentLimits.MaxPageSize}.");
    }
}
