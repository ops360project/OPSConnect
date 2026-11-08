using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsConnect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelegramSubmissionAcknowledgementTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AcknowledgementAttemptCount",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgementDeliveredAtUtc",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgementLastError",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgementMessageText",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AcknowledgementRetryAfterSeconds",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgementSendingStartedAtUtc",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgementStatus",
                schema: "telegram",
                table: "TelegramMessageLog",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "NotApplicable");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgementAttemptCount",
                schema: "telegram",
                table: "TelegramMessageLog");

            migrationBuilder.DropColumn(
                name: "AcknowledgementDeliveredAtUtc",
                schema: "telegram",
                table: "TelegramMessageLog");

            migrationBuilder.DropColumn(
                name: "AcknowledgementLastError",
                schema: "telegram",
                table: "TelegramMessageLog");

            migrationBuilder.DropColumn(
                name: "AcknowledgementMessageText",
                schema: "telegram",
                table: "TelegramMessageLog");

            migrationBuilder.DropColumn(
                name: "AcknowledgementRetryAfterSeconds",
                schema: "telegram",
                table: "TelegramMessageLog");

            migrationBuilder.DropColumn(
                name: "AcknowledgementSendingStartedAtUtc",
                schema: "telegram",
                table: "TelegramMessageLog");

            migrationBuilder.DropColumn(
                name: "AcknowledgementStatus",
                schema: "telegram",
                table: "TelegramMessageLog");
        }
    }
}
