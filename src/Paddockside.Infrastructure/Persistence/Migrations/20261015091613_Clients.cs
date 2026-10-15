using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Clients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AddedAt",
                table: "Parties",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HowWeKnowThem",
                table: "Parties",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientLists_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClientListMembers",
                columns: table => new
                {
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientListMembers", x => new { x.ClientListId, x.PartyId });
                    table.ForeignKey(
                        name: "FK_ClientListMembers_ClientLists_ClientListId",
                        column: x => x.ClientListId,
                        principalTable: "ClientLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientListMembers_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientListMembers_PartyId",
                table: "ClientListMembers",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientLists_TenantId",
                table: "ClientLists",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientLists_TenantId_Name",
                table: "ClientLists",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientListMembers");

            migrationBuilder.DropTable(
                name: "ClientLists");

            migrationBuilder.DropColumn(
                name: "AddedAt",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "HowWeKnowThem",
                table: "Parties");
        }
    }
}
