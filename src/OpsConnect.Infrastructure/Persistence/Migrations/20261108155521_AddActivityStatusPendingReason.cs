using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsConnect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityStatusPendingReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentPendingReason",
                schema: "activity",
                table: "ActivityStatus",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentPendingReasonDescription",
                schema: "activity",
                table: "ActivityStatus",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingReason",
                schema: "activity",
                table: "ActivityHistory",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingReasonDescription",
                schema: "activity",
                table: "ActivityHistory",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityStatus_Activity_Date_PendingReason",
                schema: "activity",
                table: "ActivityStatus",
                columns: new[] { "ActivityId", "ReportingDate", "CurrentPendingReason" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ActivityStatus_Activity_Date_PendingReason",
                schema: "activity",
                table: "ActivityStatus");

            migrationBuilder.DropColumn(
                name: "CurrentPendingReason",
                schema: "activity",
                table: "ActivityStatus");

            migrationBuilder.DropColumn(
                name: "CurrentPendingReasonDescription",
                schema: "activity",
                table: "ActivityStatus");

            migrationBuilder.DropColumn(
                name: "PendingReason",
                schema: "activity",
                table: "ActivityHistory");

            migrationBuilder.DropColumn(
                name: "PendingReasonDescription",
                schema: "activity",
                table: "ActivityHistory");
        }
    }
}
