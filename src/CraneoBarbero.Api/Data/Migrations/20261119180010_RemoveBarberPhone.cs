using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CraneoBarbero.Api.Data.Migrations
{
    public partial class RemoveBarberPhone : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Users\" SET \"phone\" = NULL WHERE \"role\" = 'barber' AND \"phone\" IS NOT NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_barber_without_phone",
                table: "Users",
                sql: "\"role\" <> 'barber' OR \"phone\" IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_barber_without_phone",
                table: "Users");
        }
    }
}
