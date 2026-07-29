using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.LocalDb.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnUserImagesJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserImagesJson",
                table: "turns",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserImagesJson",
                table: "turns");
        }
    }
}
