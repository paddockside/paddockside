using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DirectReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Direct",
                table: "Deliveries",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Direct",
                table: "Deliveries");
        }
    }
}
