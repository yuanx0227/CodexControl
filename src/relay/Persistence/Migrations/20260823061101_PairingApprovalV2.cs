using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexControl.Relay.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PairingApprovalV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 用户已确认当前 Pairing 均为测试数据。v2 不保留 Claim 即授权的旧关系。
            migrationBuilder.Sql("DELETE FROM Pairings;");
            migrationBuilder.Sql("DELETE FROM PairingSessions;");

            migrationBuilder.AddColumn<string>(
                name: "Alias",
                table: "Pairings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PairingRequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    ControllerId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ControllerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairingRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PairingRequests_DeviceId",
                table: "PairingRequests",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_PairingRequests_ExpiresAt",
                table: "PairingRequests",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PairingRequests_SessionId",
                table: "PairingRequests",
                column: "SessionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PairingRequests");

            migrationBuilder.DropColumn(
                name: "Alias",
                table: "Pairings");
        }
    }
}
