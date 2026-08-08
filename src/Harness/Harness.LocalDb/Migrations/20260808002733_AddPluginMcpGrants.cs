using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginMcpGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plugin_mcp_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", nullable: false),
                    Capabilities = table.Column<int>(type: "INTEGER", nullable: false),
                    PackageChecksum = table.Column<string>(type: "TEXT", nullable: false),
                    GrantedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_mcp_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plugin_mcp_grants_plugin_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "plugin_installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plugin_mcp_grants_InstallationId",
                table: "plugin_mcp_grants",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_mcp_grants_SubjectId",
                table: "plugin_mcp_grants",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_mcp_grants_SubjectId_InstallationId_ServerId",
                table: "plugin_mcp_grants",
                columns: new[] { "SubjectId", "InstallationId", "ServerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plugin_mcp_grants");
        }
    }
}
