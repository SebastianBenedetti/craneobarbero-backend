using CraneoBarbero.Api.Domain.Enums;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace CraneoBarbero.Api.Data;

public static class DatabaseSetup
{
    public static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder options)
    {
        options.MapEnum<UserRole>("user_role");
        options.MapEnum<AppointmentStatus>("appointment_status");
        options.MapEnum<PaymentStatus>("payment_status");
        options.MapEnum<PaymentMethod>("payment_method");
        options.MapEnum<OrderStatus>("order_status");
        options.MapEnum<RefundStatus>("refund_status");
        options.MapEnum<TransactionStatus>("transaction_status");
        options.MapEnum<StockMovementType>("stock_movement_type");
        options.MapEnum<NotificationType>("notification_type");
        options.MapEnum<NotificationChannel>("notification_channel");
        options.MapEnum<NotificationStatus>("notification_status");
        options.MapEnum<AuthTokenType>("auth_token_type");
        options.MapEnum<SlotOrigin>("slot_origin");
        options.MapEnum<HolidayType>("holiday_type");
        options.MapEnum<PriceEntityType>("price_entity_type");
        options.MapEnum<PriceField>("price_field");
        options.MapEnum<PriceChangeReason>("price_change_reason");
        options.MapEnum<PriceAdjustmentSource>("price_adjustment_source");
    }
}
