using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InboundEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoutingAddresses_Token",
                table: "RoutingAddresses");

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "RoutingAddresses",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(12)",
                oldUnicode: false,
                oldMaxLength: 12);

            migrationBuilder.CreateTable(
                name: "InboundMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RecipientAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Recipients = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TextBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Headers = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RawBlobName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StateAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MatchTier = table.Column<int>(type: "int", nullable: true),
                    RoutingAddressId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HorseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StreamItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DisplayBody = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboundMessages_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboundMessages_Horses_HorseId",
                        column: x => x.HorseId,
                        principalTable: "Horses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboundMessages_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboundMessages_RoutingAddresses_RoutingAddressId",
                        column: x => x.RoutingAddressId,
                        principalTable: "RoutingAddresses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboundMessages_StreamItems_StreamItemId",
                        column: x => x.StreamItemId,
                        principalTable: "StreamItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboundMessages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InboundAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    ContentId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    BlobName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DroppedReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InboundMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboundAttachments_InboundMessages_InboundMessageId",
                        column: x => x.InboundMessageId,
                        principalTable: "InboundMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingAddresses_HorseSlug",
                table: "RoutingAddresses",
                columns: new[] { "TenantId", "Token" },
                unique: true,
                filter: "[Kind] = 'HorseInbox'");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingAddresses_RecipientToken",
                table: "RoutingAddresses",
                column: "Token",
                unique: true,
                filter: "[Kind] = 'Recipient'");

            migrationBuilder.CreateIndex(
                name: "IX_InboundAttachments_InboundMessageId",
                table: "InboundAttachments",
                column: "InboundMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_EventId",
                table: "InboundMessages",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_HorseId",
                table: "InboundMessages",
                column: "HorseId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_PartyId",
                table: "InboundMessages",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_RoutingAddressId",
                table: "InboundMessages",
                column: "RoutingAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_StreamItemId",
                table: "InboundMessages",
                column: "StreamItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_TenantId",
                table: "InboundMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_TenantId_Provider_ProviderMessageId",
                table: "InboundMessages",
                columns: new[] { "TenantId", "Provider", "ProviderMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessages_TenantId_State_ReceivedAt",
                table: "InboundMessages",
                columns: new[] { "TenantId", "State", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboundAttachments");

            migrationBuilder.DropTable(
                name: "InboundMessages");

            migrationBuilder.DropIndex(
                name: "IX_RoutingAddresses_HorseSlug",
                table: "RoutingAddresses");

            migrationBuilder.DropIndex(
                name: "IX_RoutingAddresses_RecipientToken",
                table: "RoutingAddresses");

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "RoutingAddresses",
                type: "varchar(12)",
                unicode: false,
                maxLength: 12,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldUnicode: false,
                oldMaxLength: 64);

            migrationBuilder.CreateIndex(
                name: "IX_RoutingAddresses_Token",
                table: "RoutingAddresses",
                column: "Token",
                unique: true);
        }
    }
}
