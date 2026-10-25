using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeechWakeNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "speech_wake_names",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    Variant1 = table.Column<string>(type: "TEXT", nullable: false),
                    Variant2 = table.Column<string>(type: "TEXT", nullable: false),
                    Variant3 = table.Column<string>(type: "TEXT", nullable: false),
                    Variant4 = table.Column<string>(type: "TEXT", nullable: false),
                    Variant5 = table.Column<string>(type: "TEXT", nullable: false),
                    Variant6 = table.Column<string>(type: "TEXT", nullable: false),
                    Variant7 = table.Column<string>(type: "TEXT", nullable: false),
                    Variant8 = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_speech_wake_names", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_speech_wake_names_SubjectId",
                table: "speech_wake_names",
                column: "SubjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "speech_wake_names");
        }
    }
}
