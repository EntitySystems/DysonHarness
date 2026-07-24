using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenAiApiMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpenAiApiMode",
                table: "model_providers",
                type: "TEXT",
                nullable: false,
                defaultValue: "Completions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenAiApiMode",
                table: "model_providers");
        }
    }
}
