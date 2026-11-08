using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsConnect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RffoSourceMessageIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_ActivityHistory_TelegramMessageLogId",
                schema: "activity",
                table: "ActivityHistory",
                column: "TelegramMessageLogId",
                unique: true,
                filter: "[TelegramMessageLogId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ActivityHistory_TelegramMessageLogId",
                schema: "activity",
                table: "ActivityHistory");
        }
    }
}
