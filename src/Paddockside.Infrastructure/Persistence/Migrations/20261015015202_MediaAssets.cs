using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MediaAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StreamItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HorseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    BlobName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaAssets_Horses_HorseId",
                        column: x => x.HorseId,
                        principalTable: "Horses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaAssets_StreamItems_StreamItemId",
                        column: x => x.StreamItemId,
                        principalTable: "StreamItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaAssets_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_HorseId",
                table: "MediaAssets",
                column: "HorseId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_StreamItemId",
                table: "MediaAssets",
                column: "StreamItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_TenantId",
                table: "MediaAssets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_TenantId_HorseId_CreatedAt",
                table: "MediaAssets",
                columns: new[] { "TenantId", "HorseId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_TenantId_StreamItemId",
                table: "MediaAssets",
                columns: new[] { "TenantId", "StreamItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaAssets");
        }
    }
}
