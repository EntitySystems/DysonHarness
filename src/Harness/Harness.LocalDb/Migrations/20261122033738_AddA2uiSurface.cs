using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddA2uiSurface : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "A2uiSurfaceJson",
                table: "turns",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "A2uiSurfaceJson",
                table: "turns");
        }
    }
}
