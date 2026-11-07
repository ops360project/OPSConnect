using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OpsConnect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "admin");

            migrationBuilder.EnsureSchema(
                name: "activity");

            migrationBuilder.EnsureSchema(
                name: "org");

            migrationBuilder.EnsureSchema(
                name: "reporting");

            migrationBuilder.EnsureSchema(
                name: "telegram");

            migrationBuilder.CreateTable(
                name: "ActivityMaster",
                schema: "activity",
                columns: table => new
                {
                    ActivityId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ActivityName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConfigurationJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityMaster", x => x.ActivityId);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationHistory",
                schema: "admin",
                columns: table => new
                {
                    ConfigurationHistoryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EntityName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActionType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PreviousValueJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValueJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ChangedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ChangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationHistory", x => x.ConfigurationHistoryId);
                });

            migrationBuilder.CreateTable(
                name: "RoleMaster",
                schema: "admin",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleMaster", x => x.RoleId);
                });

            migrationBuilder.CreateTable(
                name: "SystemConfiguration",
                schema: "admin",
                columns: table => new
                {
                    SystemConfigurationId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConfigKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConfigValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemConfiguration", x => x.SystemConfigurationId);
                });

            migrationBuilder.CreateTable(
                name: "TelegramMessageLog",
                schema: "telegram",
                columns: table => new
                {
                    TelegramMessageLogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TelegramChatId = table.Column<long>(type: "bigint", nullable: false),
                    TelegramMessageId = table.Column<long>(type: "bigint", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: true),
                    SenderUsername = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    SenderDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MessageTimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RawMessageText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ParsedActivityCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ParsedBranchCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ParsedStatusText = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ActivityId = table.Column<int>(type: "int", nullable: true),
                    BranchId = table.Column<int>(type: "int", nullable: true),
                    ReportingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ProcessingStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RejectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReplyTelegramMessageId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramMessageLog", x => x.TelegramMessageLogId);
                });

            migrationBuilder.CreateTable(
                name: "UserMaster",
                schema: "admin",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LastLoginAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMaster", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "ZoneMaster",
                schema: "org",
                columns: table => new
                {
                    ZoneId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ZoneCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ZoneName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZoneMaster", x => x.ZoneId);
                });

            migrationBuilder.CreateTable(
                name: "ActivityConfiguration",
                schema: "admin",
                columns: table => new
                {
                    ActivityConfigurationId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    ConfigKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConfigValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityConfiguration", x => x.ActivityConfigurationId);
                    table.ForeignKey(
                        name: "FK_ActivityConfiguration_ActivityMaster_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "activity",
                        principalTable: "ActivityMaster",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActivityReportingConfiguration",
                schema: "admin",
                columns: table => new
                {
                    ConfigurationId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    ReportingStartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    ReportingEndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    ReportingIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsReportingEnabled = table.Column<bool>(type: "bit", nullable: false),
                    StopWhenAllCompleted = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityReportingConfiguration", x => x.ConfigurationId);
                    table.ForeignKey(
                        name: "FK_ActivityReportingConfiguration_ActivityMaster_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "activity",
                        principalTable: "ActivityMaster",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TelegramGroups",
                schema: "telegram",
                columns: table => new
                {
                    TelegramGroupId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    TelegramChatId = table.Column<long>(type: "bigint", nullable: false),
                    GroupTitle = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    GroupRole = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramGroups", x => x.TelegramGroupId);
                    table.ForeignKey(
                        name: "FK_TelegramGroups_ActivityMaster_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "activity",
                        principalTable: "ActivityMaster",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserRoleMapping",
                schema: "admin",
                columns: table => new
                {
                    UserRoleMappingId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoleMapping", x => x.UserRoleMappingId);
                    table.ForeignKey(
                        name: "FK_UserRoleMapping_RoleMaster_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "admin",
                        principalTable: "RoleMaster",
                        principalColumn: "RoleId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoleMapping_UserMaster_UserId",
                        column: x => x.UserId,
                        principalSchema: "admin",
                        principalTable: "UserMaster",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StateMaster",
                schema: "org",
                columns: table => new
                {
                    StateId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ZoneId = table.Column<int>(type: "int", nullable: false),
                    StateCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StateName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StateMaster", x => x.StateId);
                    table.ForeignKey(
                        name: "FK_StateMaster_ZoneMaster_ZoneId",
                        column: x => x.ZoneId,
                        principalSchema: "org",
                        principalTable: "ZoneMaster",
                        principalColumn: "ZoneId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegionMaster",
                schema: "org",
                columns: table => new
                {
                    RegionId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StateId = table.Column<int>(type: "int", nullable: false),
                    RegionCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RegionName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegionMaster", x => x.RegionId);
                    table.ForeignKey(
                        name: "FK_RegionMaster_StateMaster_StateId",
                        column: x => x.StateId,
                        principalSchema: "org",
                        principalTable: "StateMaster",
                        principalColumn: "StateId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BranchMaster",
                schema: "org",
                columns: table => new
                {
                    BranchId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ZoneId = table.Column<int>(type: "int", nullable: false),
                    StateId = table.Column<int>(type: "int", nullable: false),
                    RegionId = table.Column<int>(type: "int", nullable: false),
                    BranchCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BranchName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchMaster", x => x.BranchId);
                    table.ForeignKey(
                        name: "FK_BranchMaster_RegionMaster_RegionId",
                        column: x => x.RegionId,
                        principalSchema: "org",
                        principalTable: "RegionMaster",
                        principalColumn: "RegionId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BranchMaster_StateMaster_StateId",
                        column: x => x.StateId,
                        principalSchema: "org",
                        principalTable: "StateMaster",
                        principalColumn: "StateId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BranchMaster_ZoneMaster_ZoneId",
                        column: x => x.ZoneId,
                        principalSchema: "org",
                        principalTable: "ZoneMaster",
                        principalColumn: "ZoneId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityStatus",
                schema: "activity",
                columns: table => new
                {
                    ActivityStatusId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    BranchId = table.Column<int>(type: "int", nullable: false),
                    ReportingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrentStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LastTelegramMessageLogId = table.Column<long>(type: "bigint", nullable: true),
                    LastUpdatedByTelegramUserId = table.Column<long>(type: "bigint", nullable: true),
                    LastUpdatedByDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReopenedCount = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityStatus", x => x.ActivityStatusId);
                    table.ForeignKey(
                        name: "FK_ActivityStatus_ActivityMaster_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "activity",
                        principalTable: "ActivityMaster",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivityStatus_BranchMaster_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "org",
                        principalTable: "BranchMaster",
                        principalColumn: "BranchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrganisationMappingHistory",
                schema: "org",
                columns: table => new
                {
                    MappingHistoryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BranchId = table.Column<int>(type: "int", nullable: false),
                    BranchCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ZoneId = table.Column<int>(type: "int", nullable: false),
                    ZoneCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ZoneName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    StateId = table.Column<int>(type: "int", nullable: false),
                    StateCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StateName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    RegionId = table.Column<int>(type: "int", nullable: false),
                    RegionCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RegionName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    EffectiveFromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveToDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ChangeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganisationMappingHistory", x => x.MappingHistoryId);
                    table.ForeignKey(
                        name: "FK_OrganisationMappingHistory_BranchMaster_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "org",
                        principalTable: "BranchMaster",
                        principalColumn: "BranchId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusReopenPermission",
                schema: "activity",
                columns: table => new
                {
                    PermissionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BranchId = table.Column<int>(type: "int", nullable: false),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    ReportingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EnabledBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    EnabledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsumedByTelegramUserId = table.Column<long>(type: "bigint", nullable: true),
                    ConsumedMessageId = table.Column<long>(type: "bigint", nullable: true),
                    PermissionStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusReopenPermission", x => x.PermissionId);
                    table.ForeignKey(
                        name: "FK_StatusReopenPermission_ActivityMaster_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "activity",
                        principalTable: "ActivityMaster",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StatusReopenPermission_BranchMaster_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "org",
                        principalTable: "BranchMaster",
                        principalColumn: "BranchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityHistory",
                schema: "activity",
                columns: table => new
                {
                    ActivityHistoryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    BranchId = table.Column<int>(type: "int", nullable: false),
                    ReportingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PreviousStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NewStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TelegramMessageLogId = table.Column<long>(type: "bigint", nullable: true),
                    ConsumedReopenPermissionId = table.Column<long>(type: "bigint", nullable: true),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: true),
                    TelegramUsername = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    TelegramDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityHistory", x => x.ActivityHistoryId);
                    table.ForeignKey(
                        name: "FK_ActivityHistory_ActivityMaster_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "activity",
                        principalTable: "ActivityMaster",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivityHistory_BranchMaster_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "org",
                        principalTable: "BranchMaster",
                        principalColumn: "BranchId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivityHistory_StatusReopenPermission_ConsumedReopenPermissionId",
                        column: x => x.ConsumedReopenPermissionId,
                        principalSchema: "activity",
                        principalTable: "StatusReopenPermission",
                        principalColumn: "PermissionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportingCycle",
                schema: "reporting",
                columns: table => new
                {
                    ReportingCycleId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    ReportingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CycleStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StopReason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletionSequenceNumber = table.Column<int>(type: "int", nullable: false),
                    LastReactivatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastReactivatedByBranchId = table.Column<int>(type: "int", nullable: true),
                    LastReactivatedByPermissionId = table.Column<long>(type: "bigint", nullable: true),
                    LastReactivationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportingCycle", x => x.ReportingCycleId);
                    table.ForeignKey(
                        name: "FK_ReportingCycle_ActivityMaster_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "activity",
                        principalTable: "ActivityMaster",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReportingCycle_BranchMaster_LastReactivatedByBranchId",
                        column: x => x.LastReactivatedByBranchId,
                        principalSchema: "org",
                        principalTable: "BranchMaster",
                        principalColumn: "BranchId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReportingCycle_StatusReopenPermission_LastReactivatedByPermissionId",
                        column: x => x.LastReactivatedByPermissionId,
                        principalSchema: "activity",
                        principalTable: "StatusReopenPermission",
                        principalColumn: "PermissionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportingSlot",
                schema: "reporting",
                columns: table => new
                {
                    ReportingSlotId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportingCycleId = table.Column<long>(type: "bigint", nullable: false),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    ReportingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ScheduledSlotTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    ScheduledExecutionUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SlotStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    HangfireJobId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExecutedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StatusNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportingSlot", x => x.ReportingSlotId);
                    table.ForeignKey(
                        name: "FK_ReportingSlot_ActivityMaster_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "activity",
                        principalTable: "ActivityMaster",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReportingSlot_ReportingCycle_ReportingCycleId",
                        column: x => x.ReportingCycleId,
                        principalSchema: "reporting",
                        principalTable: "ReportingCycle",
                        principalColumn: "ReportingCycleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SummaryLog",
                schema: "reporting",
                columns: table => new
                {
                    ReportId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportingCycleId = table.Column<long>(type: "bigint", nullable: false),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    ReportingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TriggerType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ScheduledSlotTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TotalActiveBranches = table.Column<int>(type: "int", nullable: false),
                    CompletedBranches = table.Column<int>(type: "int", nullable: false),
                    PendingBranches = table.Column<int>(type: "int", nullable: false),
                    NotReportedBranches = table.Column<int>(type: "int", nullable: false),
                    CompletionPercentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    FormattedReportContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SummaryLog", x => x.ReportId);
                    table.ForeignKey(
                        name: "FK_SummaryLog_ActivityMaster_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "activity",
                        principalTable: "ActivityMaster",
                        principalColumn: "ActivityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SummaryLog_ReportingCycle_ReportingCycleId",
                        column: x => x.ReportingCycleId,
                        principalSchema: "reporting",
                        principalTable: "ReportingCycle",
                        principalColumn: "ReportingCycleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportDeliveryLog",
                schema: "reporting",
                columns: table => new
                {
                    ReportDeliveryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportId = table.Column<long>(type: "bigint", nullable: false),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    ReportingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ScheduledSlotTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    DestinationGroupId = table.Column<int>(type: "int", nullable: false),
                    DestinationTelegramChatId = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    DeliveryStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DeliveryAttempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DeliveredTelegramMessageIds = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDeliveryLog", x => x.ReportDeliveryId);
                    table.ForeignKey(
                        name: "FK_ReportDeliveryLog_SummaryLog_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "reporting",
                        principalTable: "SummaryLog",
                        principalColumn: "ReportId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReportDeliveryLog_TelegramGroups_DestinationGroupId",
                        column: x => x.DestinationGroupId,
                        principalSchema: "telegram",
                        principalTable: "TelegramGroups",
                        principalColumn: "TelegramGroupId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "activity",
                table: "ActivityMaster",
                columns: new[] { "ActivityId", "ActivityCode", "ActivityName", "ConfigurationJson", "CreatedAtUtc", "CreatedBy", "Description", "IsActive", "UpdatedAtUtc", "UpdatedBy" },
                values: new object[] { 1, "RFFO", "Request for Field Force Operations", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SystemSeed", "Daily branch operational status monitoring and reporting for RFFO.", true, null, null });

            migrationBuilder.InsertData(
                schema: "admin",
                table: "RoleMaster",
                columns: new[] { "RoleId", "CreatedAtUtc", "CreatedBy", "Description", "IsActive", "RoleName", "UpdatedAtUtc", "UpdatedBy" },
                values: new object[,]
                {
                    { 1, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SystemSeed", "Full administrative control over OpsConnect configuration, users, and operations.", true, "SuperAdmin", null, null },
                    { 2, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SystemSeed", "Operational administrator authorised to manage schedules, branches, and status reopening.", true, "Admin", null, null },
                    { 3, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SystemSeed", "Read-only access to dashboards, summaries, and audit logs.", true, "Viewer", null, null }
                });

            migrationBuilder.InsertData(
                schema: "admin",
                table: "ActivityReportingConfiguration",
                columns: new[] { "ConfigurationId", "ActivityId", "CreatedAtUtc", "CreatedBy", "EffectiveFrom", "EffectiveTo", "IsReportingEnabled", "ReportingEndTime", "ReportingIntervalMinutes", "ReportingStartTime", "StopWhenAllCompleted", "TimeZoneId", "UpdatedAtUtc", "UpdatedBy" },
                values: new object[] { 1, 1, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SystemSeed", new DateOnly(2026, 1, 1), null, true, new TimeOnly(18, 0, 0), 30, new TimeOnly(9, 0, 0), true, "Asia/Kolkata", null, null });

            migrationBuilder.CreateIndex(
                name: "UX_ActivityConfiguration_ActivityId_ConfigKey",
                schema: "admin",
                table: "ActivityConfiguration",
                columns: new[] { "ActivityId", "ConfigKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityHistory_Activity_Branch_Date",
                schema: "activity",
                table: "ActivityHistory",
                columns: new[] { "ActivityId", "BranchId", "ReportingDate", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityHistory_BranchId",
                schema: "activity",
                table: "ActivityHistory",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityHistory_ConsumedReopenPermissionId",
                schema: "activity",
                table: "ActivityHistory",
                column: "ConsumedReopenPermissionId");

            migrationBuilder.CreateIndex(
                name: "UX_ActivityMaster_ActivityCode",
                schema: "activity",
                table: "ActivityMaster",
                column: "ActivityCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReportingConfiguration_Activity_Effective",
                schema: "admin",
                table: "ActivityReportingConfiguration",
                columns: new[] { "ActivityId", "EffectiveFrom", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityStatus_Activity_Date_Status",
                schema: "activity",
                table: "ActivityStatus",
                columns: new[] { "ActivityId", "ReportingDate", "CurrentStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityStatus_BranchId",
                schema: "activity",
                table: "ActivityStatus",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "UX_ActivityStatus_Activity_Branch_Date",
                schema: "activity",
                table: "ActivityStatus",
                columns: new[] { "ActivityId", "BranchId", "ReportingDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BranchMaster_Active_State_Region",
                schema: "org",
                table: "BranchMaster",
                columns: new[] { "IsActive", "StateId", "RegionId" });

            migrationBuilder.CreateIndex(
                name: "IX_BranchMaster_RegionId",
                schema: "org",
                table: "BranchMaster",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_BranchMaster_StateId",
                schema: "org",
                table: "BranchMaster",
                column: "StateId");

            migrationBuilder.CreateIndex(
                name: "IX_BranchMaster_ZoneId",
                schema: "org",
                table: "BranchMaster",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "UX_BranchMaster_BranchCode",
                schema: "org",
                table: "BranchMaster",
                column: "BranchCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationHistory_Entity_ChangedAtUtc",
                schema: "admin",
                table: "ConfigurationHistory",
                columns: new[] { "EntityName", "ChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganisationMappingHistory_BranchId_EffectiveFromDate",
                schema: "org",
                table: "OrganisationMappingHistory",
                columns: new[] { "BranchId", "EffectiveFromDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RegionMaster_StateId",
                schema: "org",
                table: "RegionMaster",
                column: "StateId");

            migrationBuilder.CreateIndex(
                name: "UX_RegionMaster_RegionCode",
                schema: "org",
                table: "RegionMaster",
                column: "RegionCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportDeliveryLog_DestinationGroupId",
                schema: "reporting",
                table: "ReportDeliveryLog",
                column: "DestinationGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDeliveryLog_ReportId",
                schema: "reporting",
                table: "ReportDeliveryLog",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDeliveryLog_Status_Date",
                schema: "reporting",
                table: "ReportDeliveryLog",
                columns: new[] { "DeliveryStatus", "ReportingDate" });

            migrationBuilder.CreateIndex(
                name: "UX_ReportDeliveryLog_IdempotencyKey",
                schema: "reporting",
                table: "ReportDeliveryLog",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportingCycle_LastReactivatedByBranchId",
                schema: "reporting",
                table: "ReportingCycle",
                column: "LastReactivatedByBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportingCycle_LastReactivatedByPermissionId",
                schema: "reporting",
                table: "ReportingCycle",
                column: "LastReactivatedByPermissionId");

            migrationBuilder.CreateIndex(
                name: "UX_ReportingCycle_Activity_Date",
                schema: "reporting",
                table: "ReportingCycle",
                columns: new[] { "ActivityId", "ReportingDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportingSlot_ReportingCycleId",
                schema: "reporting",
                table: "ReportingSlot",
                column: "ReportingCycleId");

            migrationBuilder.CreateIndex(
                name: "UX_ReportingSlot_Activity_Date_SlotTime",
                schema: "reporting",
                table: "ReportingSlot",
                columns: new[] { "ActivityId", "ReportingDate", "ScheduledSlotTime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RoleMaster_RoleName",
                schema: "admin",
                table: "RoleMaster",
                column: "RoleName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StateMaster_ZoneId",
                schema: "org",
                table: "StateMaster",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "UX_StateMaster_StateCode",
                schema: "org",
                table: "StateMaster",
                column: "StateCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusReopenPermission_ActivityId",
                schema: "activity",
                table: "StatusReopenPermission",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusReopenPermission_Lookup",
                schema: "activity",
                table: "StatusReopenPermission",
                columns: new[] { "BranchId", "ActivityId", "ReportingDate", "PermissionStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_SummaryLog_Activity_Date_GeneratedAt",
                schema: "reporting",
                table: "SummaryLog",
                columns: new[] { "ActivityId", "ReportingDate", "GeneratedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SummaryLog_ReportingCycleId",
                schema: "reporting",
                table: "SummaryLog",
                column: "ReportingCycleId");

            migrationBuilder.CreateIndex(
                name: "UX_SummaryLog_IdempotencyKey",
                schema: "reporting",
                table: "SummaryLog",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SystemConfiguration_ConfigKey",
                schema: "admin",
                table: "SystemConfiguration",
                column: "ConfigKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramGroups_ActivityId",
                schema: "telegram",
                table: "TelegramGroups",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "UX_TelegramGroups_ChatId_Activity_Role",
                schema: "telegram",
                table: "TelegramGroups",
                columns: new[] { "TelegramChatId", "ActivityId", "GroupRole" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramMessageLog_Date_Activity_Branch",
                schema: "telegram",
                table: "TelegramMessageLog",
                columns: new[] { "ReportingDate", "ActivityId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "UX_TelegramMessageLog_ChatId_MessageId",
                schema: "telegram",
                table: "TelegramMessageLog",
                columns: new[] { "TelegramChatId", "TelegramMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_UserMaster_Email",
                schema: "admin",
                table: "UserMaster",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_UserMaster_Username",
                schema: "admin",
                table: "UserMaster",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleMapping_RoleId",
                schema: "admin",
                table: "UserRoleMapping",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "UX_UserRoleMapping_UserId_RoleId",
                schema: "admin",
                table: "UserRoleMapping",
                columns: new[] { "UserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ZoneMaster_ZoneCode",
                schema: "org",
                table: "ZoneMaster",
                column: "ZoneCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityConfiguration",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "ActivityHistory",
                schema: "activity");

            migrationBuilder.DropTable(
                name: "ActivityReportingConfiguration",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "ActivityStatus",
                schema: "activity");

            migrationBuilder.DropTable(
                name: "ConfigurationHistory",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "OrganisationMappingHistory",
                schema: "org");

            migrationBuilder.DropTable(
                name: "ReportDeliveryLog",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "ReportingSlot",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "SystemConfiguration",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "TelegramMessageLog",
                schema: "telegram");

            migrationBuilder.DropTable(
                name: "UserRoleMapping",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "SummaryLog",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "TelegramGroups",
                schema: "telegram");

            migrationBuilder.DropTable(
                name: "RoleMaster",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "UserMaster",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "ReportingCycle",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "StatusReopenPermission",
                schema: "activity");

            migrationBuilder.DropTable(
                name: "ActivityMaster",
                schema: "activity");

            migrationBuilder.DropTable(
                name: "BranchMaster",
                schema: "org");

            migrationBuilder.DropTable(
                name: "RegionMaster",
                schema: "org");

            migrationBuilder.DropTable(
                name: "StateMaster",
                schema: "org");

            migrationBuilder.DropTable(
                name: "ZoneMaster",
                schema: "org");
        }
    }
}
