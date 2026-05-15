using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QwenWeb.Migrations
{
    /// <inheritdoc />
    public partial class InitialBrowserOnlySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitorProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SearchUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    RegionCode = table.Column<string>(type: "TEXT", nullable: true),
                    LawType = table.Column<string>(type: "TEXT", nullable: true),
                    PollIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFoundCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RegNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Link = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    PubDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InitialPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    CustomerName = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: true),
                    RegionCode = table.Column<string>(type: "TEXT", nullable: true),
                    LawType = table.Column<string>(type: "TEXT", nullable: true),
                    SearchKeywords = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tenders_MonitorProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "MonitorProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenders_ProfileId",
                table: "Tenders",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenders_RegNumber_ProfileId",
                table: "Tenders",
                columns: new[] { "RegNumber", "ProfileId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tenders");

            migrationBuilder.DropTable(
                name: "MonitorProfiles");
        }
    }
}
