using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecureDataRequestJson",
                table: "turns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "pending_user_questions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "session_secrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Secret = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_secrets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_secrets_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_secrets_SessionId_Key",
                table: "session_secrets",
                columns: new[] { "SessionId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_secrets_SubjectId_SessionId",
                table: "session_secrets",
                columns: new[] { "SubjectId", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_secrets");

            migrationBuilder.DropColumn(
                name: "SecureDataRequestJson",
                table: "turns");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "pending_user_questions");
        }
    }
}
