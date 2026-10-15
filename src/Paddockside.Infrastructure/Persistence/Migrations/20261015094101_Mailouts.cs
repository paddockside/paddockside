using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Mailouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UnsubscribedAt",
                table: "Parties",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MailoutId",
                table: "InboundMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Mailouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: false),
                    Audience = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SentBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HorseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mailouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Mailouts_Horses_HorseId",
                        column: x => x.HorseId,
                        principalTable: "Horses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Mailouts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MailoutRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MailoutId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StatusAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailoutRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailoutRecipients_Mailouts_MailoutId",
                        column: x => x.MailoutId,
                        principalTable: "Mailouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailoutRecipients_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailoutRecipients_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MailoutReplies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MailoutId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InboundMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailoutReplies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailoutReplies_InboundMessages_InboundMessageId",
                        column: x => x.InboundMessageId,
                        principalTable: "InboundMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailoutReplies_MailoutRecipients_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "MailoutRecipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailoutReplies_Mailouts_MailoutId",
                        column: x => x.MailoutId,
                        principalTable: "Mailouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailoutReplies_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailoutReplies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_MailoutId",
                table: "InboundMessages",
                column: "MailoutId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutRecipients_MailoutId_PartyId",
                table: "MailoutRecipients",
                columns: new[] { "MailoutId", "PartyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailoutRecipients_PartyId",
                table: "MailoutRecipients",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutRecipients_TenantId",
                table: "MailoutRecipients",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutRecipients_TenantId_Status",
                table: "MailoutRecipients",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MailoutRecipients_Token",
                table: "MailoutRecipients",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailoutReplies_InboundMessageId",
                table: "MailoutReplies",
                column: "InboundMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutReplies_MailoutId",
                table: "MailoutReplies",
                column: "MailoutId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutReplies_PartyId",
                table: "MailoutReplies",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutReplies_RecipientId",
                table: "MailoutReplies",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutReplies_TenantId",
                table: "MailoutReplies",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Mailouts_HorseId",
                table: "Mailouts",
                column: "HorseId");

            migrationBuilder.CreateIndex(
                name: "IX_Mailouts_TenantId",
                table: "Mailouts",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_InboundMessages_Mailouts_MailoutId",
                table: "InboundMessages",
                column: "MailoutId",
                principalTable: "Mailouts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InboundMessages_Mailouts_MailoutId",
                table: "InboundMessages");

            migrationBuilder.DropTable(
                name: "MailoutReplies");

            migrationBuilder.DropTable(
                name: "MailoutRecipients");

            migrationBuilder.DropTable(
                name: "Mailouts");

            migrationBuilder.DropIndex(
                name: "IX_InboundMessages_MailoutId",
                table: "InboundMessages");

            migrationBuilder.DropColumn(
                name: "UnsubscribedAt",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "MailoutId",
                table: "InboundMessages");
        }
    }
}
