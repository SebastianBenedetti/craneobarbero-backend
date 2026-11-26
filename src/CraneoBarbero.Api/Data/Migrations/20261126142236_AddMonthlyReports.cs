using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CraneoBarbero.Api.Data.Migrations
{
    public partial class AddMonthlyReports : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonthlyReports",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    periodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    periodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    aiGenerated = table.Column<bool>(type: "boolean", nullable: false),
                    emailsSent = table.Column<int>(type: "integer", nullable: false),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyReports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyReports_month",
                table: "MonthlyReports",
                column: "month",
                unique: true);

            migrationBuilder.Sql("ALTER TABLE \"MonthlyReports\" ENABLE ROW LEVEL SECURITY;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonthlyReports");
        }
    }
}
