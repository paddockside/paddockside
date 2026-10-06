using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnerAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InviteOwnersOnFirstInterest",
                table: "Tenants",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PersonId",
                table: "Parties",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OwnerInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HorseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Address = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerInvitations_Horses_HorseId",
                        column: x => x.HorseId,
                        principalTable: "Horses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerInvitations_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerInvitations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Parties_PersonId",
                table: "Parties",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerInvitations_HorseId",
                table: "OwnerInvitations",
                column: "HorseId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerInvitations_PartyId",
                table: "OwnerInvitations",
                column: "PartyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerInvitations_TenantId",
                table: "OwnerInvitations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerInvitations_TenantId_Status",
                table: "OwnerInvitations",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerInvitations");

            migrationBuilder.DropIndex(
                name: "IX_Parties_PersonId",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "InviteOwnersOnFirstInterest",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PersonId",
                table: "Parties");
        }
    }
}
