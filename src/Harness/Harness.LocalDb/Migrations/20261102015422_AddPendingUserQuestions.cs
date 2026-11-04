using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingUserQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_user_questions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QuestionGuid = table.Column<Guid>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    ChoicesJson = table.Column<string>(type: "TEXT", nullable: false),
                    MultiSelect = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowCustomAnswer = table.Column<bool>(type: "INTEGER", nullable: false),
                    SubagentId = table.Column<int>(type: "INTEGER", nullable: true),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_user_questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pending_user_questions_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_user_questions_SessionId",
                table: "pending_user_questions",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_pending_user_questions_SubjectId_SessionId",
                table: "pending_user_questions",
                columns: new[] { "SubjectId", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_user_questions");
        }
    }
}
