using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsConnect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelegramMessageProcessingConcurrencyAndState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessingStartedAtUtc",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessingStartedAtUtc",
                schema: "telegram",
                table: "TelegramMessageLog");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "telegram",
                table: "TelegramMessageLog");
        }
    }
}
