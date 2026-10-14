using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paddockside.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HouseShareParty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HousePartyId",
                table: "Tenants",
                type: "uniqueidentifier",
                nullable: true);

            // Businesses set up before this: their house share is the organisation named for it ("Laurel Oak house
            // share"). Only where there is exactly one; anything ambiguous is left for an operator to set.
            migrationBuilder.Sql("""
                UPDATE t SET HousePartyId = (SELECT p.Id FROM Parties p WHERE p.TenantId = t.Id AND p.Kind = 'Organisation' AND p.DisplayName LIKE '%house share%')
                FROM Tenants t
                WHERE (SELECT COUNT(*) FROM Parties q WHERE q.TenantId = t.Id AND q.Kind = 'Organisation' AND q.DisplayName LIKE '%house share%') = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HousePartyId",
                table: "Tenants");
        }
    }
}
