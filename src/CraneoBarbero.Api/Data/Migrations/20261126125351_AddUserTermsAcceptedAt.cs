using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CraneoBarbero.Api.Data.Migrations
{
    public partial class AddUserTermsAcceptedAt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "termsAcceptedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "termsAcceptedAt",
                table: "Users");
        }
    }
}
