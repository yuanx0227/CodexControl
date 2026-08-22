using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexControl.Relay.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPairingLookupHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodeLookupHash",
                table: "PairingSessions",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // 旧会话没有可重建的查找摘要；升级时安全失效，设备可重新生成三分钟配对码。
            migrationBuilder.Sql(
                "UPDATE PairingSessions SET ConsumedAt = CreatedAt WHERE ConsumedAt IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_PairingSessions_CodeLookupHash",
                table: "PairingSessions",
                column: "CodeLookupHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PairingSessions_CodeLookupHash",
                table: "PairingSessions");

            migrationBuilder.DropColumn(
                name: "CodeLookupHash",
                table: "PairingSessions");
        }
    }
}
