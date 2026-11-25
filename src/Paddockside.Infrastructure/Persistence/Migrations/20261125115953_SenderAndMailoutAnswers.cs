using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SenderAndMailoutAnswers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SenderAddress",
                table: "Tenants",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderName",
                table: "Tenants",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderProblem",
                table: "Tenants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SenderTestedAt",
                table: "Tenants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SendsAsSender",
                table: "Tenants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HandledAt",
                table: "MailoutReplies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetAsideBy",
                table: "MailoutReplies",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MailoutAnswers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MailoutId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReplyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Signature = table.Column<string>(type: "nvarchar(1400)", maxLength: 1400, nullable: false),
                    Author = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StatusAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailoutAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailoutAnswers_MailoutRecipients_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "MailoutRecipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailoutAnswers_MailoutReplies_ReplyId",
                        column: x => x.ReplyId,
                        principalTable: "MailoutReplies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailoutAnswers_Mailouts_MailoutId",
                        column: x => x.MailoutId,
                        principalTable: "Mailouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailoutAnswers_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MailoutAnswers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailoutAnswers_MailoutId",
                table: "MailoutAnswers",
                column: "MailoutId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutAnswers_PartyId",
                table: "MailoutAnswers",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutAnswers_RecipientId",
                table: "MailoutAnswers",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutAnswers_ReplyId",
                table: "MailoutAnswers",
                column: "ReplyId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutAnswers_TenantId",
                table: "MailoutAnswers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MailoutAnswers_TenantId_Status",
                table: "MailoutAnswers",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailoutAnswers");

            migrationBuilder.DropColumn(
                name: "SenderAddress",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SenderName",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SenderProblem",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SenderTestedAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SendsAsSender",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "HandledAt",
                table: "MailoutReplies");

            migrationBuilder.DropColumn(
                name: "SetAsideBy",
                table: "MailoutReplies");
        }
    }
}
