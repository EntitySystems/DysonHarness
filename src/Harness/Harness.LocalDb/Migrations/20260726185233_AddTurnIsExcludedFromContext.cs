using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.LocalDb.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnIsExcludedFromContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsExcludedFromContext",
                table: "turns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsExcludedFromContext",
                table: "turns");
        }
    }
}
