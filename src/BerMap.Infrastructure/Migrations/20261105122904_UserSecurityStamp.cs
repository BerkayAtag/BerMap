using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BerMap.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UserSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Every existing account gets its own stamp. Tokens issued before
            // this migration carry no stamp and are rejected, so users sign in
            // again once after the upgrade.
            migrationBuilder.Sql(
                """UPDATE "Users" SET "SecurityStamp" = md5(random()::text || clock_timestamp()::text || "Id"::text);""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Users");
        }
    }
}
