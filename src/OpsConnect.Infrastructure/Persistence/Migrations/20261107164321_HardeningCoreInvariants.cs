using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsConnect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardeningCoreInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReportDeliveryLog_ReportId",
                schema: "reporting",
                table: "ReportDeliveryLog");

            migrationBuilder.CreateIndex(
                name: "UX_StatusReopenPermission_ActivePerBranchDate",
                schema: "activity",
                table: "StatusReopenPermission",
                columns: new[] { "BranchId", "ActivityId", "ReportingDate" },
                unique: true,
                filter: "[PermissionStatus] = 'Available'");

            migrationBuilder.CreateIndex(
                name: "UX_ReportDeliveryLog_ReportId_DestinationGroupId",
                schema: "reporting",
                table: "ReportDeliveryLog",
                columns: new[] { "ReportId", "DestinationGroupId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_StatusReopenPermission_ActivePerBranchDate",
                schema: "activity",
                table: "StatusReopenPermission");

            migrationBuilder.DropIndex(
                name: "UX_ReportDeliveryLog_ReportId_DestinationGroupId",
                schema: "reporting",
                table: "ReportDeliveryLog");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDeliveryLog_ReportId",
                schema: "reporting",
                table: "ReportDeliveryLog",
                column: "ReportId");
        }
    }
}
