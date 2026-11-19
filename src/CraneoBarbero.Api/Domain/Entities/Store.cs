using CraneoBarbero.Api.Domain.Enums;

namespace CraneoBarbero.Api.Domain.Entities;

public class ProductCategory : IHasTimestamps
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class Supplier : IHasTimestamps
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? TaxId { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class Product : IHasTimestamps
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public decimal SalePrice { get; set; }
    public decimal Cost { get; set; }
    public int Stock { get; set; }
    public int MinStock { get; set; }
    public int? ReorderQuantity { get; set; }
    public int? DefaultSupplierId { get; set; }
    public string? ImageUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ProductCategory? Category { get; set; }
    public Supplier? DefaultSupplier { get; set; }
}

public class Order : IHasTimestamps
{
    public int Id { get; set; }
    public int? ClientId { get; set; }
    public int BranchId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.PendingPayment;
    public decimal Total { get; set; }
    public RefundStatus RefundStatus { get; set; } = RefundStatus.None;
    public DateTimeOffset? PaymentExpiresAt { get; set; }
    public string? CancellationReason { get; set; }
    public int? CancelledById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public User? Client { get; set; }
    public Branch? Branch { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal? UnitCost { get; set; }

    public Order? Order { get; set; }
    public Product? Product { get; set; }
}

public class StockMovement : IHasCreatedAt
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public StockMovementType Type { get; set; }
    public int Quantity { get; set; }
    public decimal? UnitCost { get; set; }
    public int? SupplierId { get; set; }
    public string? DocumentNumber { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public int BalanceAfter { get; set; }
    public int? OrderId { get; set; }
    public int UserId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Product? Product { get; set; }
}
