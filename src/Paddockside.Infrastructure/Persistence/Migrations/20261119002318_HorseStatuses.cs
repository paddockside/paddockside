using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HorseStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HorseStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HorseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Next = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExpectedBack = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ExpectedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    SetBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    SetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HorseStatuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HorseStatuses_Horses_HorseId",
                        column: x => x.HorseId,
                        principalTable: "Horses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HorseStatuses_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HorseStatuses_HorseId_SetAt",
                table: "HorseStatuses",
                columns: new[] { "HorseId", "SetAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HorseStatuses_TenantId",
                table: "HorseStatuses",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HorseStatuses");
        }
    }
}
