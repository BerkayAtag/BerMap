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
