using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MediaSharedFrom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SharedFromId",
                table: "MediaAssets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_TenantId_SharedFromId",
                table: "MediaAssets",
                columns: new[] { "TenantId", "SharedFromId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_TenantId_SharedFromId",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "SharedFromId",
                table: "MediaAssets");
        }
    }
}
