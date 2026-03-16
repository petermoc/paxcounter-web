using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaxCounterWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddGpsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Altitude",
                table: "PaxSamples",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Hdop",
                table: "PaxSamples",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "PaxSamples",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "PaxSamples",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Satellites",
                table: "PaxSamples",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Altitude",
                table: "PaxSamples");

            migrationBuilder.DropColumn(
                name: "Hdop",
                table: "PaxSamples");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "PaxSamples");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "PaxSamples");

            migrationBuilder.DropColumn(
                name: "Satellites",
                table: "PaxSamples");
        }
    }
}
