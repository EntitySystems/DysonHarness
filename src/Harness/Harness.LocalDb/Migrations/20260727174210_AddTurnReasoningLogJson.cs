using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.LocalDb.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnReasoningLogJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReasoningLogJson",
                table: "turns",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReasoningLogJson",
                table: "turns");
        }
    }
}
