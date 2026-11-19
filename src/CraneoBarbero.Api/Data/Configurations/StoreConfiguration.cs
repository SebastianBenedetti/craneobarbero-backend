using CraneoBarbero.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Data.Configurations;

public static class StoreConfiguration
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductCategory>(entity =>
        {
            entity.ToTable("ProductCategories");
            entity.Property(c => c.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(c => c.Name).IsUnique();
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.ToTable("Suppliers");
            entity.Property(s => s.Name).IsRequired().HasMaxLength(150);
            entity.Property(s => s.ContactName).HasMaxLength(100);
            entity.Property(s => s.Phone).HasMaxLength(30);
            entity.Property(s => s.Email).HasMaxLength(254);
            entity.Property(s => s.TaxId).HasMaxLength(20);
            entity.Property(s => s.Notes).HasColumnType("text");
            entity.Property(s => s.IsActive).HasDefaultValue(true).HasSentinel(true);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products", table =>
            {
                table.HasCheckConstraint("CK_Products_salePrice", "\"salePrice\" >= 0");
                table.HasCheckConstraint("CK_Products_cost", "\"cost\" >= 0");
                table.HasCheckConstraint("CK_Products_stock", "\"stock\" >= 0");
                table.HasCheckConstraint("CK_Products_minStock", "\"minStock\" >= 0");
                table.HasCheckConstraint("CK_Products_reorderQuantity", "\"reorderQuantity\" IS NULL OR \"reorderQuantity\" > 0");
            });
            entity.Property(p => p.Name).IsRequired().HasMaxLength(150);
            entity.Property(p => p.Description).HasColumnType("text");
            entity.Property(p => p.Sku).HasMaxLength(50);
            entity.HasIndex(p => p.Sku).IsUnique();
            entity.Property(p => p.Barcode).HasMaxLength(50);
            entity.Property(p => p.SalePrice).HasPrecision(10, 2);
            entity.Property(p => p.Cost).HasPrecision(10, 2).HasDefaultValue(0m);
            entity.Property(p => p.Stock).HasDefaultValue(0);
            entity.Property(p => p.MinStock).HasDefaultValue(0);
            entity.Property(p => p.ImageUrl).HasMaxLength(500);
            entity.Property(p => p.IsActive).HasDefaultValue(true).HasSentinel(true);
            entity.HasOne(p => p.Category).WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(p => p.DefaultSupplier).WithMany().HasForeignKey(p => p.DefaultSupplierId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders", table => table.HasCheckConstraint("CK_Orders_total", "\"total\" >= 0"));
            entity.Property(o => o.Total).HasPrecision(10, 2);
            entity.Property(o => o.CancellationReason).HasMaxLength(300);
            entity.HasOne(o => o.Client).WithMany().HasForeignKey(o => o.ClientId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(o => o.Branch).WithMany().HasForeignKey(o => o.BranchId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(o => o.CancelledById).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(o => new { o.Status, o.PaymentExpiresAt });
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems", table =>
            {
                table.HasCheckConstraint("CK_OrderItems_quantity", "\"quantity\" > 0");
                table.HasCheckConstraint("CK_OrderItems_unitPrice", "\"unitPrice\" >= 0");
            });
            entity.Property(i => i.UnitPrice).HasPrecision(10, 2);
            entity.Property(i => i.UnitCost).HasPrecision(10, 2);
            entity.HasIndex(i => new { i.OrderId, i.ProductId }).IsUnique();
            entity.HasOne(i => i.Order).WithMany(o => o.Items).HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(i => i.Product).WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockMovement>(entity =>
        {
            entity.ToTable("StockMovements", table =>
            {
                table.HasCheckConstraint("CK_StockMovements_quantity", "\"quantity\" <> 0");
                table.HasCheckConstraint("CK_StockMovements_balanceAfter", "\"balanceAfter\" >= 0");
                table.HasCheckConstraint("CK_StockMovements_in_cost", "\"type\" <> 'in' OR \"unitCost\" IS NOT NULL");
                table.HasCheckConstraint("CK_StockMovements_adjustment_reason", "\"type\" <> 'adjustment' OR \"reason\" IS NOT NULL");
            });
            entity.Property(m => m.UnitCost).HasPrecision(10, 2);
            entity.Property(m => m.DocumentNumber).HasMaxLength(50);
            entity.Property(m => m.Reason).HasMaxLength(300);
            entity.HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Supplier>().WithMany().HasForeignKey(m => m.SupplierId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Order>().WithMany().HasForeignKey(m => m.OrderId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(m => new { m.ProductId, m.OccurredAt });
        });

        modelBuilder.Entity<PriceAdjustment>(entity =>
        {
            entity.ToTable("PriceAdjustments", table =>
                table.HasCheckConstraint("CK_PriceAdjustments_scopeType", "\"scopeType\" IN ('all', 'category', 'selection')"));
            entity.Property(a => a.Percentage).HasPrecision(7, 4);
            entity.Property(a => a.ScopeType).IsRequired().HasMaxLength(20);
            entity.Property(a => a.ScopeDescription).HasMaxLength(300);
            entity.Property(a => a.RoundingMode).IsRequired().HasMaxLength(20);
            entity.HasOne<User>().WithMany().HasForeignKey(a => a.AppliedById).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(a => a.RevertedById).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PriceHistory>(entity =>
        {
            entity.ToTable("PriceHistory");
            entity.Property(h => h.OldPrice).HasPrecision(10, 2);
            entity.Property(h => h.NewPrice).HasPrecision(10, 2);
            entity.HasOne<PriceAdjustment>().WithMany().HasForeignKey(h => h.AdjustmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(h => h.ChangedById).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(h => new { h.EntityType, h.EntityId, h.ChangedAt });
        });
    }
}
