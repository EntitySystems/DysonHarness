using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Engine.Migrations
{
    /// <inheritdoc />
    public partial class SplitModelProfilesToProvidersAndSlugs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sessions_models_ModelProfileId",
                table: "sessions");

            migrationBuilder.CreateTable(
                name: "model_providers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKind = table.Column<string>(type: "TEXT", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "model_slugs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayAlias = table.Column<string>(type: "TEXT", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_slugs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_model_slugs_model_providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "model_providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // One provider per old profile; slug Id = old profile Id so session FKs remap 1:1.
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE "__model_split_map" (
                    "ProfileId" TEXT NOT NULL PRIMARY KEY,
                    "ProviderId" TEXT NOT NULL
                );

                INSERT INTO "__model_split_map" ("ProfileId", "ProviderId")
                SELECT
                    "Id",
                    lower(
                        hex(randomblob(4)) || '-' ||
                        hex(randomblob(2)) || '-4' ||
                        substr(hex(randomblob(2)), 2) || '-' ||
                        substr('89ab', abs(random()) % 4 + 1, 1) ||
                        substr(hex(randomblob(2)), 2) || '-' ||
                        hex(randomblob(6))
                    )
                FROM "models";

                INSERT INTO "model_providers" ("Id", "DisplayName", "ProviderKind", "BaseUrl", "ApiKey", "CreatedUtc", "UpdatedUtc")
                SELECT
                    map."ProviderId",
                    m."DisplayName",
                    m."ProviderKind",
                    m."BaseUrl",
                    m."ApiKey",
                    m."CreatedUtc",
                    m."UpdatedUtc"
                FROM "models" AS m
                INNER JOIN "__model_split_map" AS map ON m."Id" = map."ProfileId";

                INSERT INTO "model_slugs" ("Id", "ProviderId", "Slug", "DisplayAlias", "IsDefault", "CreatedUtc", "UpdatedUtc")
                SELECT
                    m."Id",
                    map."ProviderId",
                    m."ModelId",
                    m."DisplayName",
                    m."IsDefault",
                    m."CreatedUtc",
                    m."UpdatedUtc"
                FROM "models" AS m
                INNER JOIN "__model_split_map" AS map ON m."Id" = map."ProfileId";

                DROP TABLE "__model_split_map";
                """);

            migrationBuilder.RenameColumn(
                name: "ModelProfileId",
                table: "sessions",
                newName: "ModelSlugId");

            migrationBuilder.RenameIndex(
                name: "IX_sessions_ModelProfileId",
                table: "sessions",
                newName: "IX_sessions_ModelSlugId");

            migrationBuilder.DropTable(
                name: "models");

            migrationBuilder.CreateIndex(
                name: "IX_model_slugs_IsDefault",
                table: "model_slugs",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_model_slugs_ProviderId_Slug",
                table: "model_slugs",
                columns: new[] { "ProviderId", "Slug" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_sessions_model_slugs_ModelSlugId",
                table: "sessions",
                column: "ModelSlugId",
                principalTable: "model_slugs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sessions_model_slugs_ModelSlugId",
                table: "sessions");

            migrationBuilder.CreateTable(
                name: "models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKind = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_models", x => x.Id);
                });

            // Flatten each slug back into a profile row (Id preserved = session FK still valid).
            migrationBuilder.Sql(
                """
                INSERT INTO "models" ("Id", "DisplayName", "ProviderKind", "ModelId", "BaseUrl", "ApiKey", "IsDefault", "CreatedUtc", "UpdatedUtc")
                SELECT
                    s."Id",
                    s."DisplayAlias",
                    p."ProviderKind",
                    s."Slug",
                    p."BaseUrl",
                    p."ApiKey",
                    s."IsDefault",
                    s."CreatedUtc",
                    s."UpdatedUtc"
                FROM "model_slugs" AS s
                INNER JOIN "model_providers" AS p ON s."ProviderId" = p."Id";
                """);

            migrationBuilder.DropTable(
                name: "model_slugs");

            migrationBuilder.DropTable(
                name: "model_providers");

            migrationBuilder.RenameColumn(
                name: "ModelSlugId",
                table: "sessions",
                newName: "ModelProfileId");

            migrationBuilder.RenameIndex(
                name: "IX_sessions_ModelSlugId",
                table: "sessions",
                newName: "IX_sessions_ModelProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_models_IsDefault",
                table: "models",
                column: "IsDefault");

            migrationBuilder.AddForeignKey(
                name: "FK_sessions_models_ModelProfileId",
                table: "sessions",
                column: "ModelProfileId",
                principalTable: "models",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
