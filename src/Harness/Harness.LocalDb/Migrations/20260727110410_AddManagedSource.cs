using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.LocalDb.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ManagedSource",
                table: "model_providers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_providers_ManagedSource",
                table: "model_providers",
                column: "ManagedSource",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_model_providers_ManagedSource",
                table: "model_providers");

            migrationBuilder.DropColumn(
                name: "ManagedSource",
                table: "model_providers");
        }
    }
}
