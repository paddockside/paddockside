using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmailBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrandColour",
                table: "Tenants",
                type: "varchar(7)",
                unicode: false,
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LogoHeight",
                table: "Tenants",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LogoWidth",
                table: "Tenants",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "StaffSignatures",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailCopyTried",
                table: "MediaAssets",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EmailToken",
                table: "MediaAssets",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BrandColour",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LogoHeight",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LogoWidth",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "StaffSignatures");

            migrationBuilder.DropColumn(
                name: "EmailCopyTried",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "EmailToken",
                table: "MediaAssets");
        }
    }
}
