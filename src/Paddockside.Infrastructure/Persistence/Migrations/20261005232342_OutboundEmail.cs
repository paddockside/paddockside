using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboundEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Deliveries_StreamItemId",
                table: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_TenantId_StreamItemId",
                table: "Deliveries");

            migrationBuilder.AddColumn<string>(
                name: "FooterDetails",
                table: "Tenants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "Tenants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Tenants",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Deliveries",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "Deliveries",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Deliveries",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderMessageId",
                table: "Deliveries",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RoutingAddressId",
                table: "Deliveries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SentAt",
                table: "Deliveries",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PartyContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    UndeliverableSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartyContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartyContacts_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoutingAddresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Token = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    HorseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StreamItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingAddresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoutingAddresses_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoutingAddresses_Horses_HorseId",
                        column: x => x.HorseId,
                        principalTable: "Horses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoutingAddresses_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoutingAddresses_StreamItems_StreamItemId",
                        column: x => x.StreamItemId,
                        principalTable: "StreamItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoutingAddresses_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true,
                filter: "[Slug] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_ProviderMessageId",
                table: "Deliveries",
                column: "ProviderMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_RoutingAddressId",
                table: "Deliveries",
                column: "RoutingAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_StreamItemId_PartyId_Channel",
                table: "Deliveries",
                columns: new[] { "StreamItemId", "PartyId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_TenantId_Status_Channel",
                table: "Deliveries",
                columns: new[] { "TenantId", "Status", "Channel" });

            migrationBuilder.CreateIndex(
                name: "IX_PartyContacts_PartyId",
                table: "PartyContacts",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "IX_PartyContacts_Value",
                table: "PartyContacts",
                column: "Value");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingAddresses_EventId",
                table: "RoutingAddresses",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingAddresses_HorseId",
                table: "RoutingAddresses",
                column: "HorseId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingAddresses_PartyId",
                table: "RoutingAddresses",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingAddresses_StreamItemId",
                table: "RoutingAddresses",
                column: "StreamItemId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingAddresses_TenantId",
                table: "RoutingAddresses",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingAddresses_Token",
                table: "RoutingAddresses",
                column: "Token",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Deliveries_RoutingAddresses_RoutingAddressId",
                table: "Deliveries",
                column: "RoutingAddressId",
                principalTable: "RoutingAddresses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deliveries_RoutingAddresses_RoutingAddressId",
                table: "Deliveries");

            migrationBuilder.DropTable(
                name: "PartyContacts");

            migrationBuilder.DropTable(
                name: "RoutingAddresses");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_ProviderMessageId",
                table: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_RoutingAddressId",
                table: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_StreamItemId_PartyId_Channel",
                table: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_TenantId_Status_Channel",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "FooterDetails",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Address",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "ProviderMessageId",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "RoutingAddressId",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "Deliveries");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_StreamItemId",
                table: "Deliveries",
                column: "StreamItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_TenantId_StreamItemId",
                table: "Deliveries",
                columns: new[] { "TenantId", "StreamItemId" });
        }
    }
}
