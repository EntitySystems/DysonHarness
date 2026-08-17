using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnInterruptionReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InterruptionReason",
                table: "turns",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InterruptionReason",
                table: "turns");
        }
    }
}
