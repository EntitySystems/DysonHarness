using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usage_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkDirectoryName = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RootSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelSlug = table.Column<string>(type: "TEXT", nullable: false),
                    ModelDisplayAlias = table.Column<string>(type: "TEXT", nullable: false),
                    ReasoningEffort = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CacheTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    WriteTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CacheWriteTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTokensAfterCache = table.Column<int>(type: "INTEGER", nullable: false),
                    WriteTokensAfterCache = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_usage_requests_SubjectId_OccurredUtc",
                table: "usage_requests",
                columns: new[] { "SubjectId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_usage_requests_SubjectId_RootSessionId",
                table: "usage_requests",
                columns: new[] { "SubjectId", "RootSessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_usage_requests_SubjectId_WorkDirectoryName",
                table: "usage_requests",
                columns: new[] { "SubjectId", "WorkDirectoryName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_requests");
        }
    }
}
