using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Engine.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkDirectories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkDirectoryId",
                table: "sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "work_directories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    AbsolutePath = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastOpenedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_directories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_WorkDirectoryId",
                table: "sessions",
                column: "WorkDirectoryId");

            migrationBuilder.CreateIndex(
                name: "IX_work_directories_AbsolutePath",
                table: "work_directories",
                column: "AbsolutePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_directories_LastOpenedUtc",
                table: "work_directories",
                column: "LastOpenedUtc");

            migrationBuilder.AddForeignKey(
                name: "FK_sessions_work_directories_WorkDirectoryId",
                table: "sessions",
                column: "WorkDirectoryId",
                principalTable: "work_directories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sessions_work_directories_WorkDirectoryId",
                table: "sessions");

            migrationBuilder.DropTable(
                name: "work_directories");

            migrationBuilder.DropIndex(
                name: "IX_sessions_WorkDirectoryId",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "WorkDirectoryId",
                table: "sessions");
        }
    }
}
