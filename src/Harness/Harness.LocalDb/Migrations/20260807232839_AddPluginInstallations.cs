using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.LocalDb.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginInstallations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plugin_installations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedPluginId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    SourceLocation = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedRef = table.Column<string>(type: "TEXT", nullable: true),
                    SourceSubdirectory = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedCommit = table.Column<string>(type: "TEXT", nullable: true),
                    ContentChecksum = table.Column<string>(type: "TEXT", nullable: true),
                    PackageFormat = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", nullable: true),
                    InstallScope = table.Column<string>(type: "TEXT", nullable: false),
                    WorkDirectoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PackageRoot = table.Column<string>(type: "TEXT", nullable: false),
                    ComponentInventoryJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationSchemaJson = table.Column<string>(type: "TEXT", nullable: true),
                    DiagnosticsJson = table.Column<string>(type: "TEXT", nullable: false),
                    InstalledUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_installations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plugin_installations_work_directories_WorkDirectoryId",
                        column: x => x.WorkDirectoryId,
                        principalTable: "work_directories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plugin_installations_SubjectId",
                table: "plugin_installations",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_installations_SubjectId_NormalizedPluginId",
                table: "plugin_installations",
                columns: new[] { "SubjectId", "NormalizedPluginId" },
                unique: true,
                filter: "\"InstallScope\" = 'Global'");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_installations_SubjectId_NormalizedPluginId_WorkDirectoryId",
                table: "plugin_installations",
                columns: new[] { "SubjectId", "NormalizedPluginId", "WorkDirectoryId" },
                unique: true,
                filter: "\"InstallScope\" = 'Project'");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_installations_SubjectId_PackageRoot",
                table: "plugin_installations",
                columns: new[] { "SubjectId", "PackageRoot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_installations_WorkDirectoryId",
                table: "plugin_installations",
                column: "WorkDirectoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plugin_installations");
        }
    }
}
