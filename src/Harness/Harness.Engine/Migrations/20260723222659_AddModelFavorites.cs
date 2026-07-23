using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddModelFavorites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_favorites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelSlugId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_favorites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_model_favorites_model_slugs_ModelSlugId",
                        column: x => x.ModelSlugId,
                        principalTable: "model_slugs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_favorites_ModelSlugId",
                table: "model_favorites",
                column: "ModelSlugId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_favorites");
        }
    }
}
