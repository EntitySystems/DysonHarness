using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DysonHarness.Migrations
{
    /// <inheritdoc />
    public partial class AddPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkDirectoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Markdown = table.Column<string>(type: "TEXT", nullable: true),
                    PlanRelativePath = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    BuildAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plans_work_directories_WorkDirectoryId",
                        column: x => x.WorkDirectoryId,
                        principalTable: "work_directories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plans_WorkDirectoryId_PlanRelativePath",
                table: "plans",
                columns: new[] { "WorkDirectoryId", "PlanRelativePath" },
                unique: true,
                filter: "\"PlanRelativePath\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plans");
        }
    }
}
