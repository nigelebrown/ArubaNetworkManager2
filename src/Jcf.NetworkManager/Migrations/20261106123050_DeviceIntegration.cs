using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jcf.NetworkManager.Migrations
{
    /// <inheritdoc />
    public partial class DeviceIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSimulated",
                table: "Devices",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TlsCertSha256",
                table: "Devices",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TlsPinnedBy",
                table: "Devices",
                type: "nvarchar(180)",
                maxLength: 180,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TlsPinnedUtc",
                table: "Devices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeviceCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProtectedUsername = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ProtectedPassword = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCredentials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCredentials_Reference",
                table: "DeviceCredentials",
                column: "Reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceCredentials");

            migrationBuilder.DropColumn(
                name: "IsSimulated",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TlsCertSha256",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TlsPinnedBy",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TlsPinnedUtc",
                table: "Devices");
        }
    }
}
