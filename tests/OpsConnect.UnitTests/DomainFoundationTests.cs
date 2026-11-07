using OpsConnect.Application.Common;
using OpsConnect.Domain.Activities;
using OpsConnect.Domain.Administration;
using OpsConnect.Domain.Common;
using OpsConnect.Domain.Organisation;
using OpsConnect.Domain.Reporting;
using OpsConnect.Domain.Telegram;

namespace OpsConnect.UnitTests;

public class DomainFoundationTests
{
    [Fact]
    public void DatabaseSchemas_ShouldMatchRequiredSchemaNames()
    {
        Assert.Equal("org", DatabaseSchemas.Organisation);
        Assert.Equal("activity", DatabaseSchemas.Activity);
        Assert.Equal("telegram", DatabaseSchemas.Telegram);
        Assert.Equal("reporting", DatabaseSchemas.Reporting);
        Assert.Equal("admin", DatabaseSchemas.Administration);
    }

    [Fact]
    public void SystemBusinessClock_ShouldConvertUtcToAsiaKolkataCorrectly()
    {
        var clock = new SystemBusinessClock();
        // 2026-11-06 20:00:00 UTC is 2026-11-07 01:30:00 IST (+05:30)
        var utcTime = new DateTime(2026, 11, 6, 20, 0, 0, DateTimeKind.Utc);

        var istOffset = clock.ConvertUtcToTimeZone(utcTime, "Asia/Kolkata");

        Assert.Equal(TimeSpan.FromHours(5.5), istOffset.Offset);
        Assert.Equal(new DateOnly(2026, 11, 7), DateOnly.FromDateTime(istOffset.DateTime));
    }

    [Fact]
    public void ActivityStatus_ShouldDefaultToNotReported()
    {
        var status = new ActivityStatus
        {
            ActivityId = 1,
            BranchId = 10,
            ReportingDate = new DateOnly(2026, 11, 7)
        };

        Assert.Equal(BranchActivityStatus.NotReported, status.CurrentStatus);
        Assert.Equal(0, status.ReopenedCount);
    }

    [Fact]
    public void StatusReopenPermission_ShouldDefaultToAvailable()
    {
        var permission = new StatusReopenPermission
        {
            BranchId = 10,
            ActivityId = 1,
            ReportingDate = new DateOnly(2026, 11, 7),
            EnabledBy = "admin",
            Reason = "Branch reported Completed by mistake"
        };

        Assert.Equal(ReopenPermissionStatus.Available, permission.PermissionStatus);
        Assert.Null(permission.ConsumedAtUtc);
    }

    [Fact]
    public void ActivityReportingConfiguration_ShouldHaveExpectedRffoDefaults()
    {
        var config = new ActivityReportingConfiguration
        {
            ActivityId = 1,
            EffectiveFrom = new DateOnly(2026, 1, 1)
        };

        Assert.Equal(new TimeOnly(9, 0), config.ReportingStartTime);
        Assert.Equal(new TimeOnly(18, 0), config.ReportingEndTime);
        Assert.Equal(30, config.ReportingIntervalMinutes);
        Assert.Equal("Asia/Kolkata", config.TimeZoneId);
        Assert.True(config.IsReportingEnabled);
        Assert.True(config.StopWhenAllCompleted);
    }

    [Fact]
    public void ReportingCycle_ShouldInitializeAsActiveWithNoStopReason()
    {
        var cycle = new ReportingCycle
        {
            ActivityId = 1,
            ReportingDate = new DateOnly(2026, 11, 7)
        };

        Assert.Equal(ReportingCycleStatus.Active, cycle.CycleStatus);
        Assert.Equal(ReportingCycleStopReason.None, cycle.StopReason);
        Assert.Null(cycle.CompletedAtUtc);
    }

    [Fact]
    public void OrganisationEntities_ShouldDefaultToActive()
    {
        var zone = new ZoneMaster { ZoneCode = "Z1", ZoneName = "East Zone" };
        var state = new StateMaster { StateCode = "OD", StateName = "Odisha" };
        var region = new RegionMaster { RegionCode = "R1", RegionName = "Region A" };
        var branch = new BranchMaster { BranchCode = "36:01", BranchName = "Bhubaneswar Main" };
        var group = new TelegramGroup { TelegramChatId = -1001234567890, GroupTitle = "RFFO Source", GroupRole = TelegramGroupRole.Source };

        Assert.True(zone.IsActive);
        Assert.True(state.IsActive);
        Assert.True(region.IsActive);
        Assert.True(branch.IsActive);
        Assert.True(group.IsActive);
    }
}
