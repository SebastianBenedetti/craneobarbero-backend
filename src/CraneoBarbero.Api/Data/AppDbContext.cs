using CraneoBarbero.Api.Data.Configurations;
using CraneoBarbero.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Cut> Cuts => Set<Cut>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<PriceAdjustment> PriceAdjustments => Set<PriceAdjustment>();
    public DbSet<PriceHistory> PriceHistory => Set<PriceHistory>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuthToken> AuthTokens => Set<AuthToken>();
    public DbSet<AvailableSlot> AvailableSlots => Set<AvailableSlot>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<MonthlyReport> MonthlyReports => Set<MonthlyReport>();
    public DbSet<StoredImage> Images => Set<StoredImage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("extensions", "btree_gist");

        UsersAndCatalogConfiguration.Apply(modelBuilder);
        AppointmentsConfiguration.Apply(modelBuilder);
        StoreConfiguration.Apply(modelBuilder);
        SupportConfiguration.Apply(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.GetColumnName() == property.Name)
                {
                    property.SetColumnName(char.ToLowerInvariant(property.Name[0]) + property.Name[1..]);
                }
            }
        }
    }
}
