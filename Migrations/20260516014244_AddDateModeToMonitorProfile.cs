using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QwenWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddDateModeToMonitorProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CustomDate",
                table: "MonitorProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseCustomDate",
                table: "MonitorProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomDate",
                table: "MonitorProfiles");

            migrationBuilder.DropColumn(
                name: "UseCustomDate",
                table: "MonitorProfiles");
        }
    }
}
