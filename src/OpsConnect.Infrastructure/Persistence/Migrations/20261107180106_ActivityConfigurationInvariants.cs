using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsConnect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActivityConfigurationInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RevocationReason",
                schema: "activity",
                table: "StatusReopenPermission",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAtUtc",
                schema: "activity",
                table: "StatusReopenPermission",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedBy",
                schema: "activity",
                table: "StatusReopenPermission",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "admin",
                table: "ActivityReportingConfiguration",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "UX_ActivityReportingConfiguration_ActivePerActivity",
                schema: "admin",
                table: "ActivityReportingConfiguration",
                column: "ActivityId",
                unique: true,
                filter: "[EffectiveTo] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ActivityReportingConfiguration_ActivePerActivity",
                schema: "admin",
                table: "ActivityReportingConfiguration");

            migrationBuilder.DropColumn(
                name: "RevocationReason",
                schema: "activity",
                table: "StatusReopenPermission");

            migrationBuilder.DropColumn(
                name: "RevokedAtUtc",
                schema: "activity",
                table: "StatusReopenPermission");

            migrationBuilder.DropColumn(
                name: "RevokedBy",
                schema: "activity",
                table: "StatusReopenPermission");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "admin",
                table: "ActivityReportingConfiguration");
        }
    }
}
