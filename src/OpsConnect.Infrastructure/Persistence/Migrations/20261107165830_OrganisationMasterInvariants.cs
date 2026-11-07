using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsConnect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganisationMasterInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "org",
                table: "OrganisationMappingHistory",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "org",
                table: "BranchMaster",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "UX_OrganisationMappingHistory_ActivePerBranch",
                schema: "org",
                table: "OrganisationMappingHistory",
                column: "BranchId",
                unique: true,
                filter: "[EffectiveToDate] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_OrganisationMappingHistory_ActivePerBranch",
                schema: "org",
                table: "OrganisationMappingHistory");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "org",
                table: "OrganisationMappingHistory");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "org",
                table: "BranchMaster");
        }
    }
}
