using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaxCounterWeb.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaxSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WifiCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RssiLimit = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaxSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaxSamples_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RssiSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PaxSampleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rssi = table.Column<int>(type: "INTEGER", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RssiSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RssiSamples_PaxSamples_PaxSampleId",
                        column: x => x.PaxSampleId,
                        principalTable: "PaxSamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaxSamples_DeviceId",
                table: "PaxSamples",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_RssiSamples_PaxSampleId",
                table: "RssiSamples",
                column: "PaxSampleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RssiSamples");

            migrationBuilder.DropTable(
                name: "PaxSamples");

            migrationBuilder.DropTable(
                name: "Devices");
        }
    }
}
