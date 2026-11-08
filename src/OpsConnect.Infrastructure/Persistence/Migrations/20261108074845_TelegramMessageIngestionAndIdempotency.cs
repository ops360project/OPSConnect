using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsConnect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelegramMessageIngestionAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceGroupTitle",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "nvarchar(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceTelegramGroupId",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TelegramUpdateId",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_TelegramMessageLog_TelegramUpdateId",
                schema: "telegram",
                table: "TelegramMessageLog",
                column: "TelegramUpdateId",
                unique: true,
                filter: "[TelegramUpdateId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_TelegramMessageLog_TelegramUpdateId",
                schema: "telegram",
                table: "TelegramMessageLog");

            migrationBuilder.DropColumn(
                name: "SourceGroupTitle",
                schema: "telegram",
                table: "TelegramMessageLog");

            migrationBuilder.DropColumn(
                name: "SourceTelegramGroupId",
                schema: "telegram",
                table: "TelegramMessageLog");

            migrationBuilder.DropColumn(
                name: "TelegramUpdateId",
                schema: "telegram",
                table: "TelegramMessageLog");
        }
    }
}
