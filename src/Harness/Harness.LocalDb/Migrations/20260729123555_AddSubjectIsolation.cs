using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.LocalDb.Migrations
{
    /// <inheritdoc />
    public partial class AddSubjectIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_work_directories_AbsolutePath",
                table: "work_directories");

            migrationBuilder.DropIndex(
                name: "IX_model_providers_ManagedSource",
                table: "model_providers");

            migrationBuilder.DropIndex(
                name: "IX_model_favorites_ModelSlugId",
                table: "model_favorites");

            migrationBuilder.DropIndex(
                name: "IX_configured_shells_Name",
                table: "configured_shells");

            migrationBuilder.DropPrimaryKey(
                name: "PK_app_settings",
                table: "app_settings");

            // Existing rows backfill to desktop subject "local".
            migrationBuilder.AddColumn<string>(
                name: "SubjectId",
                table: "work_directories",
                type: "TEXT",
                nullable: false,
                defaultValue: "local");

            migrationBuilder.AddColumn<string>(
                name: "SubjectId",
                table: "sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: "local");

            migrationBuilder.AddColumn<string>(
                name: "SubjectId",
                table: "model_providers",
                type: "TEXT",
                nullable: false,
                defaultValue: "local");

            migrationBuilder.AddColumn<string>(
                name: "SubjectId",
                table: "model_favorites",
                type: "TEXT",
                nullable: false,
                defaultValue: "local");

            migrationBuilder.AddColumn<string>(
                name: "SubjectId",
                table: "configured_shells",
                type: "TEXT",
                nullable: false,
                defaultValue: "local");

            migrationBuilder.AddColumn<string>(
                name: "SubjectId",
                table: "app_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "local");

            migrationBuilder.AddPrimaryKey(
                name: "PK_app_settings",
                table: "app_settings",
                columns: new[] { "SubjectId", "Key" });

            migrationBuilder.CreateTable(
                name: "subjects",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subjects", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "subjects",
                columns: new[] { "Id", "CreatedUtc", "UserId" },
                values: new object[] { "local", DateTime.UtcNow, null });

            migrationBuilder.CreateIndex(
                name: "IX_work_directories_SubjectId",
                table: "work_directories",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_work_directories_SubjectId_AbsolutePath",
                table: "work_directories",
                columns: new[] { "SubjectId", "AbsolutePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_SubjectId",
                table: "sessions",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_model_providers_SubjectId",
                table: "model_providers",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_model_providers_SubjectId_ManagedSource",
                table: "model_providers",
                columns: new[] { "SubjectId", "ManagedSource" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_favorites_ModelSlugId",
                table: "model_favorites",
                column: "ModelSlugId");

            migrationBuilder.CreateIndex(
                name: "IX_model_favorites_SubjectId",
                table: "model_favorites",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_model_favorites_SubjectId_ModelSlugId",
                table: "model_favorites",
                columns: new[] { "SubjectId", "ModelSlugId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_configured_shells_SubjectId",
                table: "configured_shells",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_configured_shells_SubjectId_Name",
                table: "configured_shells",
                columns: new[] { "SubjectId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subjects");

            migrationBuilder.DropIndex(
                name: "IX_work_directories_SubjectId",
                table: "work_directories");

            migrationBuilder.DropIndex(
                name: "IX_work_directories_SubjectId_AbsolutePath",
                table: "work_directories");

            migrationBuilder.DropIndex(
                name: "IX_sessions_SubjectId",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "IX_model_providers_SubjectId",
                table: "model_providers");

            migrationBuilder.DropIndex(
                name: "IX_model_providers_SubjectId_ManagedSource",
                table: "model_providers");

            migrationBuilder.DropIndex(
                name: "IX_model_favorites_ModelSlugId",
                table: "model_favorites");

            migrationBuilder.DropIndex(
                name: "IX_model_favorites_SubjectId",
                table: "model_favorites");

            migrationBuilder.DropIndex(
                name: "IX_model_favorites_SubjectId_ModelSlugId",
                table: "model_favorites");

            migrationBuilder.DropIndex(
                name: "IX_configured_shells_SubjectId",
                table: "configured_shells");

            migrationBuilder.DropIndex(
                name: "IX_configured_shells_SubjectId_Name",
                table: "configured_shells");

            migrationBuilder.DropPrimaryKey(
                name: "PK_app_settings",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                table: "work_directories");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                table: "model_providers");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                table: "model_favorites");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                table: "configured_shells");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                table: "app_settings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_app_settings",
                table: "app_settings",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_work_directories_AbsolutePath",
                table: "work_directories",
                column: "AbsolutePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_providers_ManagedSource",
                table: "model_providers",
                column: "ManagedSource",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_favorites_ModelSlugId",
                table: "model_favorites",
                column: "ModelSlugId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_configured_shells_Name",
                table: "configured_shells",
                column: "Name",
                unique: true);
        }
    }
}
