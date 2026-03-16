using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaxCounterWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddBatteryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BatteryPercent",
                table: "PaxSamples",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BatteryVoltageMv",
                table: "PaxSamples",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BatteryPercent",
                table: "PaxSamples");

            migrationBuilder.DropColumn(
                name: "BatteryVoltageMv",
                table: "PaxSamples");
        }
    }
}
