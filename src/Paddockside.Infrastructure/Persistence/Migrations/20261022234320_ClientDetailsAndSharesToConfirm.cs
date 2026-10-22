using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClientDetailsAndSharesToConfirm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Parties",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Company",
                table: "Parties",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Parties",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OtherPhones",
                table: "Parties",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Social",
                table: "Parties",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceGroups",
                table: "Parties",
                type: "nvarchar(max)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Parties",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                table: "Parties",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Address",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "Company",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "OtherPhones",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "Social",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "SourceGroups",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "Website",
                table: "Parties");
        }
    }
}
