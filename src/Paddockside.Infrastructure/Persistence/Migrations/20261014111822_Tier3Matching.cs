using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Tier3Matching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MatchCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InboundMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HorseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Score = table.Column<int>(type: "int", nullable: false),
                    Reasons = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Chosen = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchCandidates_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MatchCandidates_Horses_HorseId",
                        column: x => x.HorseId,
                        principalTable: "Horses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MatchCandidates_InboundMessages_InboundMessageId",
                        column: x => x.InboundMessageId,
                        principalTable: "InboundMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MatchCandidates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_EventId",
                table: "MatchCandidates",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_HorseId",
                table: "MatchCandidates",
                column: "HorseId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_InboundMessageId",
                table: "MatchCandidates",
                column: "InboundMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_TenantId",
                table: "MatchCandidates",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchCandidates");
        }
    }
}
