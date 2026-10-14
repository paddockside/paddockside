using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NoCoOwnerSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnersSeeCoOwners",
                table: "Tenants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OwnersSeeCoOwners",
                table: "Tenants",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
