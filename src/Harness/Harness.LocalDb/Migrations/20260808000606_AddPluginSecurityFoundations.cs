using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.LocalDb.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginSecurityFoundations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plugin_hook_audits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HookComponentId = table.Column<string>(type: "TEXT", nullable: false),
                    EventName = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    DetailCode = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    InputBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_hook_audits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plugin_hook_audits_plugin_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "plugin_installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_hook_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HookComponentId = table.Column<string>(type: "TEXT", nullable: false),
                    EventName = table.Column<string>(type: "TEXT", nullable: false),
                    PermissionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FailureMode = table.Column<string>(type: "TEXT", nullable: false),
                    TimeoutMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxOutputBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    PackageChecksum = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_hook_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plugin_hook_reviews_plugin_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "plugin_installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_variable_values",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VariableName = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedValue = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_variable_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plugin_variable_values_plugin_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "plugin_installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plugin_hook_audits_InstallationId",
                table: "plugin_hook_audits",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_hook_audits_SubjectId",
                table: "plugin_hook_audits",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_hook_audits_SubjectId_InstallationId_OccurredUtc",
                table: "plugin_hook_audits",
                columns: new[] { "SubjectId", "InstallationId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_plugin_hook_reviews_InstallationId",
                table: "plugin_hook_reviews",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_hook_reviews_SubjectId",
                table: "plugin_hook_reviews",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_hook_reviews_SubjectId_InstallationId_HookComponentId_EventName",
                table: "plugin_hook_reviews",
                columns: new[] { "SubjectId", "InstallationId", "HookComponentId", "EventName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_variable_values_InstallationId",
                table: "plugin_variable_values",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_variable_values_SubjectId",
                table: "plugin_variable_values",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_variable_values_SubjectId_InstallationId_VariableName",
                table: "plugin_variable_values",
                columns: new[] { "SubjectId", "InstallationId", "VariableName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plugin_hook_audits");

            migrationBuilder.DropTable(
                name: "plugin_hook_reviews");

            migrationBuilder.DropTable(
                name: "plugin_variable_values");
        }
    }
}
