using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionWorktree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorktreeAbsolutePath",
                table: "sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorktreeBranch",
                table: "sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WorktreeEnabled",
                table: "sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorktreeAbsolutePath",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "WorktreeBranch",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "WorktreeEnabled",
                table: "sessions");
        }
    }
}
