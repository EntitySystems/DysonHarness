using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeechModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "speech_models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    RuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Capability = table.Column<int>(type: "INTEGER", nullable: false),
                    RuntimeKind = table.Column<string>(type: "TEXT", nullable: false),
                    SupportsVoicePrompt = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsInstalled = table.Column<bool>(type: "INTEGER", nullable: false),
                    InstalledVariant = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_speech_models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "speech_voices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VoiceId = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    ModelRuntimeId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    SourceFileName = table.Column<string>(type: "TEXT", nullable: true),
                    Variant = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_speech_voices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_speech_models_SubjectId",
                table: "speech_models",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_speech_models_SubjectId_RuntimeId",
                table: "speech_models",
                columns: new[] { "SubjectId", "RuntimeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_speech_voices_SubjectId",
                table: "speech_voices",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_speech_voices_SubjectId_ModelRuntimeId_VoiceId",
                table: "speech_voices",
                columns: new[] { "SubjectId", "ModelRuntimeId", "VoiceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "speech_models");

            migrationBuilder.DropTable(
                name: "speech_voices");
        }
    }
}
