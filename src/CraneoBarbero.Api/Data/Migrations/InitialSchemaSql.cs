using CraneoBarbero.Api.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CraneoBarbero.Api.Data.Migrations;

public static class InitialSchemaSql
{
    public static readonly string[] Tables =
    [
        "Users", "Branches", "Cuts", "Appointments", "Payments", "ProductCategories", "Suppliers", "Products",
        "Orders", "OrderItems", "StockMovements", "PriceAdjustments", "PriceHistory", "Reviews", "Notifications",
        "AuthTokens", "AvailableSlots", "Holidays",
    ];

    private const string ActiveStatuses = "('awaiting_payment', 'pending', 'completed')";

    public static void Apply(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            ALTER TABLE "Appointments" ADD CONSTRAINT "{AppointmentsConfiguration.BarberOverlapConstraint}"
            EXCLUDE USING gist ("barberId" WITH =, tstzrange("appointmentDate", "endDate") WITH &&)
            WHERE ("status" IN {ActiveStatuses});
            """);

        migrationBuilder.Sql($"""
            ALTER TABLE "Appointments" ADD CONSTRAINT "{AppointmentsConfiguration.ClientOverlapConstraint}"
            EXCLUDE USING gist ("clientId" WITH =, tstzrange("appointmentDate", "endDate") WITH &&)
            WHERE ("clientId" IS NOT NULL AND "status" IN {ActiveStatuses});
            """);

        foreach (var table in Tables.Append("__EFMigrationsHistory"))
        {
            migrationBuilder.Sql($"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY;");
        }
    }
}
