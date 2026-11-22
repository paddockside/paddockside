using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SocialEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SocialEventId",
                table: "Mailouts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SocialEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Venue = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Places = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialEvents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SocialAttendees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MailoutId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Places = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AnsweredBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialAttendees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialAttendees_Mailouts_MailoutId",
                        column: x => x.MailoutId,
                        principalTable: "Mailouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialAttendees_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialAttendees_SocialEvents_SocialEventId",
                        column: x => x.SocialEventId,
                        principalTable: "SocialEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialAttendees_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Mailouts_SocialEventId",
                table: "Mailouts",
                column: "SocialEventId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialAttendees_MailoutId",
                table: "SocialAttendees",
                column: "MailoutId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialAttendees_PartyId",
                table: "SocialAttendees",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialAttendees_SocialEventId_PartyId",
                table: "SocialAttendees",
                columns: new[] { "SocialEventId", "PartyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialAttendees_TenantId",
                table: "SocialAttendees",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialEvents_TenantId",
                table: "SocialEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialEvents_TenantId_StartsAt",
                table: "SocialEvents",
                columns: new[] { "TenantId", "StartsAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Mailouts_SocialEvents_SocialEventId",
                table: "Mailouts",
                column: "SocialEventId",
                principalTable: "SocialEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Mailouts_SocialEvents_SocialEventId",
                table: "Mailouts");

            migrationBuilder.DropTable(
                name: "SocialAttendees");

            migrationBuilder.DropTable(
                name: "SocialEvents");

            migrationBuilder.DropIndex(
                name: "IX_Mailouts_SocialEventId",
                table: "Mailouts");

            migrationBuilder.DropColumn(
                name: "SocialEventId",
                table: "Mailouts");
        }
    }
}
