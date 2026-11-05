using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BerMap.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NodeStatusChangeAndCollectorManaged : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CollectorManaged",
                table: "Integrations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Integrations that only ever received collector batches were fed
            // by a collector all along; without this they would stay scheduled
            // for server-side discovery after the upgrade.
            migrationBuilder.Sql(
                """
                UPDATE "Integrations" AS i
                SET "CollectorManaged" = TRUE
                WHERE EXISTS (
                        SELECT 1 FROM "DiscoveryJobs" AS j
                        WHERE j."IntegrationId" = i."Id" AND j."JobType" = 'Collector')
                  AND NOT EXISTS (
                        SELECT 1 FROM "DiscoveryJobs" AS j
                        WHERE j."IntegrationId" = i."Id" AND j."JobType" <> 'Collector');
                """);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAt",
                table: "InfrastructureNodes",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CollectorManaged",
                table: "Integrations");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "InfrastructureNodes");
        }
    }
}
