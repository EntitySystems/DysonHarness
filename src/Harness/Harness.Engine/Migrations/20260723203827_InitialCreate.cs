using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Engine.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKind = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuntimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentMode = table.Column<string>(type: "TEXT", nullable: false),
                    ModelProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    McpAccessMode = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    SystemPromptSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastActivityUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sessions_models_ModelProfileId",
                        column: x => x.ModelProfileId,
                        principalTable: "models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sessions_sessions_ParentSessionId",
                        column: x => x.ParentSessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_logs_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "turns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentTitle = table.Column<string>(type: "TEXT", nullable: true),
                    Instruction = table.Column<string>(type: "TEXT", nullable: true),
                    AssistantText = table.Column<string>(type: "TEXT", nullable: true),
                    ToolStateJson = table.Column<string>(type: "TEXT", nullable: false),
                    ToolHistoryOptimized = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompactToolHistory = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_turns_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_models_IsDefault",
                table: "models",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_session_logs_SessionId_Kind",
                table: "session_logs",
                columns: new[] { "SessionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_session_logs_SessionId_Sequence",
                table: "session_logs",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_logs_TurnId",
                table: "session_logs",
                column: "TurnId");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_LastActivityUtc",
                table: "sessions",
                column: "LastActivityUtc");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_ModelProfileId",
                table: "sessions",
                column: "ModelProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_ParentSessionId",
                table: "sessions",
                column: "ParentSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_turns_SessionId_Sequence",
                table: "turns",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_logs");

            migrationBuilder.DropTable(
                name: "turns");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "models");
        }
    }
}
