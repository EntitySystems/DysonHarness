using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddFileChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "file_changes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    RootSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WorkDirectoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RootPath = table.Column<string>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    StartLine = table.Column<int>(type: "INTEGER", nullable: false),
                    ContextBefore = table.Column<string>(type: "TEXT", nullable: true),
                    RemovedText = table.Column<string>(type: "TEXT", nullable: true),
                    AddedText = table.Column<string>(type: "TEXT", nullable: true),
                    ContextAfter = table.Column<string>(type: "TEXT", nullable: true),
                    IsTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: true),
                    WorktreeName = table.Column<string>(type: "TEXT", nullable: true),
                    BranchName = table.Column<string>(type: "TEXT", nullable: true),
                    AgentChain = table.Column<string>(type: "TEXT", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_file_changes_sessions_RootSessionId",
                        column: x => x.RootSessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_file_changes_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_file_changes_RootSessionId_Id",
                table: "file_changes",
                columns: new[] { "RootSessionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_file_changes_SessionId",
                table: "file_changes",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_file_changes_SubjectId_RootSessionId",
                table: "file_changes",
                columns: new[] { "SubjectId", "RootSessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_changes");
        }
    }
}
