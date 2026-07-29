using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.LocalDb.Migrations
{
    /// <inheritdoc />
    public partial class AddConfiguredShells : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "configured_shells",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    ExecutablePath = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configured_shells", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_configured_shells_Name",
                table: "configured_shells",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_configured_shells_SortOrder",
                table: "configured_shells",
                column: "SortOrder");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "configured_shells");
        }
    }
}
