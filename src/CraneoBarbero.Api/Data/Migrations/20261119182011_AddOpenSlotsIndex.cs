using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CraneoBarbero.Api.Data.Migrations
{
    public partial class AddOpenSlotsIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AvailableSlots_open",
                table: "AvailableSlots",
                columns: new[] { "barberId", "startAt" },
                filter: "\"deletedAt\" IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AvailableSlots_open",
                table: "AvailableSlots");
        }
    }
}
