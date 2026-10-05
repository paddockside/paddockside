using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EventStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorName",
                table: "StreamItems",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "StreamItems",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrectionNote",
                table: "StreamItems",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fields",
                table: "StreamItems",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "InReplyToId",
                table: "StreamItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "StreamItems",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesId",
                table: "StreamItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "StreamItems",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Dam",
                table: "Horses",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FoaledOn",
                table: "Horses",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sex",
                table: "Horses",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sire",
                table: "Horses",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StreamItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StatusAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deliveries_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Deliveries_StreamItems_StreamItemId",
                        column: x => x.StreamItemId,
                        principalTable: "StreamItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Deliveries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StreamItems_InReplyToId",
                table: "StreamItems",
                column: "InReplyToId");

            migrationBuilder.CreateIndex(
                name: "IX_StreamItems_SupersedesId",
                table: "StreamItems",
                column: "SupersedesId");

            migrationBuilder.CreateIndex(
                name: "IX_StreamItems_TenantId_EventId_OccurredAt",
                table: "StreamItems",
                columns: new[] { "TenantId", "EventId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_PartyId",
                table: "Deliveries",
                column: "PartyId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_StreamItemId",
                table: "Deliveries",
                column: "StreamItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_TenantId",
                table: "Deliveries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_TenantId_StreamItemId",
                table: "Deliveries",
                columns: new[] { "TenantId", "StreamItemId" });

            migrationBuilder.AddForeignKey(
                name: "FK_StreamItems_StreamItems_InReplyToId",
                table: "StreamItems",
                column: "InReplyToId",
                principalTable: "StreamItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StreamItems_StreamItems_SupersedesId",
                table: "StreamItems",
                column: "SupersedesId",
                principalTable: "StreamItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StreamItems_StreamItems_InReplyToId",
                table: "StreamItems");

            migrationBuilder.DropForeignKey(
                name: "FK_StreamItems_StreamItems_SupersedesId",
                table: "StreamItems");

            migrationBuilder.DropTable(
                name: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_StreamItems_InReplyToId",
                table: "StreamItems");

            migrationBuilder.DropIndex(
                name: "IX_StreamItems_SupersedesId",
                table: "StreamItems");

            migrationBuilder.DropIndex(
                name: "IX_StreamItems_TenantId_EventId_OccurredAt",
                table: "StreamItems");

            migrationBuilder.DropColumn(
                name: "AuthorName",
                table: "StreamItems");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "StreamItems");

            migrationBuilder.DropColumn(
                name: "CorrectionNote",
                table: "StreamItems");

            migrationBuilder.DropColumn(
                name: "Fields",
                table: "StreamItems");

            migrationBuilder.DropColumn(
                name: "InReplyToId",
                table: "StreamItems");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "StreamItems");

            migrationBuilder.DropColumn(
                name: "SupersedesId",
                table: "StreamItems");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "StreamItems");

            migrationBuilder.DropColumn(
                name: "Dam",
                table: "Horses");

            migrationBuilder.DropColumn(
                name: "FoaledOn",
                table: "Horses");

            migrationBuilder.DropColumn(
                name: "Sex",
                table: "Horses");

            migrationBuilder.DropColumn(
                name: "Sire",
                table: "Horses");
        }
    }
}
