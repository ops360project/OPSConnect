using OpsConnect.Domain.Organisation;

namespace OpsConnect.UnitTests;

public class OrganisationDomainTests
{
    private static readonly DateOnly Day1 = new(2026, 11, 1);
    private static readonly DateOnly Day10 = new(2026, 11, 10);

    private static (ZoneMaster Zone, StateMaster State, RegionMaster Region) CreateValidHierarchy()
    {
        var zone = ZoneMaster.Create(" east ", "  East Zone  ", isActive: true, createdBy: "admin");
        zone.ZoneId = 1;

        var state = StateMaster.Create(zone, " od ", " Odisha ", isActive: true, createdBy: "admin");
        state.StateId = 10;

        var region = RegionMaster.Create(state, " reg-a ", " Region A ", isActive: true, createdBy: "admin");
        region.RegionId = 100;

        return (zone, state, region);
    }

    [Fact]
    public void CreateWithInitialHistory_ShouldNormalizeCodesAndCreateSingleOpenMappingHistory()
    {
        var (zone, state, region) = CreateValidHierarchy();

        var branch = BranchMaster.CreateWithInitialHistory(
            zone: zone,
            state: state,
            region: region,
            branchCode: "  36:01 ",
            branchName: "  Bhubaneswar Main Branch  ",
            effectiveFromDate: Day1,
            isActive: true,
            createdBy: "admin.user");

        Assert.Equal("EAST", zone.ZoneCode);
        Assert.Equal("East Zone", zone.ZoneName);
        Assert.Equal("OD", state.StateCode);
        Assert.Equal("REG-A", region.RegionCode);
        Assert.Equal("36:01", branch.BranchCode);
        Assert.Equal("Bhubaneswar Main Branch", branch.BranchName);
        Assert.True(branch.IsActive);

        var history = Assert.Single(branch.MappingHistories);
        Assert.Equal("36:01", history.BranchCode);
        Assert.Equal("EAST", history.ZoneCode);
        Assert.Equal("OD", history.StateCode);
        Assert.Equal("REG-A", history.RegionCode);
        Assert.Equal(Day1, history.EffectiveFromDate);
        Assert.Null(history.EffectiveToDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("36 : 01")]
    public void NormalizeCode_WithEmptyOrInternalWhitespace_ShouldThrow(string invalidCode)
    {
        Assert.Throws<ArgumentException>(() =>
            OrganisationCodeNormalizer.NormalizeCode(invalidCode, "branchCode"));
    }

    [Fact]
    public void CreateBranch_WithMismatchedStateOrRegionHierarchy_ShouldThrow()
    {
        var (zone1, state1, region1) = CreateValidHierarchy();

        var zone2 = ZoneMaster.Create("WEST", "West Zone");
        zone2.ZoneId = 2;

        var stateInZone2 = StateMaster.Create(zone2, "MH", "Maharashtra");
        stateInZone2.StateId = 20;

        // State belongs to Zone 2, but caller passes Zone 1
        Assert.Throws<InvalidOperationException>(() =>
            BranchMaster.CreateWithInitialHistory(
                zone: zone1,
                state: stateInZone2,
                region: region1,
                branchCode: "36:01",
                branchName: "Test Branch",
                effectiveFromDate: Day1));

        // Region belongs to State 1, but caller passes State 2
        var regionInState2 = RegionMaster.Create(stateInZone2, "REG-W", "Region West");
        regionInState2.RegionId = 200;

        Assert.Throws<InvalidOperationException>(() =>
            BranchMaster.CreateWithInitialHistory(
                zone: zone1,
                state: state1,
                region: regionInState2,
                branchCode: "36:01",
                branchName: "Test Branch",
                effectiveFromDate: Day1));
    }

    [Fact]
    public void ActiveHierarchyRules_ShouldRejectActiveChildUnderInactiveParent()
    {
        var inactiveZone = ZoneMaster.Create("EAST", "East Zone", isActive: false);
        inactiveZone.ZoneId = 1;

        Assert.Throws<InvalidOperationException>(() =>
            StateMaster.Create(inactiveZone, "OD", "Odisha", isActive: true));

        var (zone, state, region) = CreateValidHierarchy();
        region.IsActive = false;

        Assert.Throws<InvalidOperationException>(() =>
            BranchMaster.CreateWithInitialHistory(
                zone: zone,
                state: state,
                region: region,
                branchCode: "36:01",
                branchName: "Test Branch",
                effectiveFromDate: Day1,
                isActive: true));

        // Inactive branch under inactive parent is allowed for historical/decommissioned records
        var inactiveBranch = BranchMaster.CreateWithInitialHistory(
            zone: zone,
            state: state,
            region: region,
            branchCode: "36:01",
            branchName: "Decommissioned Branch",
            effectiveFromDate: Day1,
            isActive: false);

        Assert.False(inactiveBranch.IsActive);
        Assert.False(inactiveBranch.CanSubmitActivityUpdate(out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void ApplyHierarchyAndDetailsUpdate_WhenMovedToNewRegion_ShouldPreserveHistoricalMappingAndOpenNewMapping()
    {
        var (zone, state, regionA) = CreateValidHierarchy();
        var regionB = RegionMaster.Create(state, "REG-B", "Region B", isActive: true);
        regionB.RegionId = 101;

        var branch = BranchMaster.CreateWithInitialHistory(
            zone: zone,
            state: state,
            region: regionA,
            branchCode: "36:01",
            branchName: "Bhubaneswar Main",
            effectiveFromDate: Day1);

        // Moving branch from Region A to Region B effective Day 10 (2026-11-10)
        var changed = branch.ApplyHierarchyAndDetailsUpdate(
            zone: zone,
            state: state,
            region: regionB,
            branchName: "Bhubaneswar Main",
            isActive: true,
            effectiveDate: Day10,
            updatedBy: "admin.ops",
            changeReason: "Regional boundary realignment");

        Assert.True(changed);
        Assert.Equal(2, branch.MappingHistories.Count);
        Assert.Single(branch.MappingHistories, h => h.EffectiveToDate == null);

        // Historical date (2026-11-05) must still resolve to Region A
        var historicalMapping = branch.ResolveMappingAsOf(new DateOnly(2026, 11, 5));
        Assert.NotNull(historicalMapping);
        Assert.Equal("REG-A", historicalMapping.RegionCode);
        Assert.Equal(new DateOnly(2026, 11, 9), historicalMapping.EffectiveToDate);

        // Effective date (2026-11-10) and later must resolve to Region B
        var currentMapping = branch.ResolveMappingAsOf(new DateOnly(2026, 11, 10));
        Assert.NotNull(currentMapping);
        Assert.Equal("REG-B", currentMapping.RegionCode);
        Assert.Null(currentMapping.EffectiveToDate);
    }

    [Fact]
    public void ApplyHierarchyAndDetailsUpdate_WhenOnlyBranchNameChanges_ShouldNotCreateNewHistoryRow()
    {
        var (zone, state, regionA) = CreateValidHierarchy();

        var branch = BranchMaster.CreateWithInitialHistory(
            zone: zone,
            state: state,
            region: regionA,
            branchCode: "36:01",
            branchName: "Old Branch Name",
            effectiveFromDate: Day1);

        var hierarchyChanged = branch.ApplyHierarchyAndDetailsUpdate(
            zone: zone,
            state: state,
            region: regionA,
            branchName: "Updated Branch Name",
            isActive: true,
            effectiveDate: Day10,
            updatedBy: "admin.ops");

        Assert.False(hierarchyChanged);
        Assert.Equal("Updated Branch Name", branch.BranchName);
        Assert.Single(branch.MappingHistories);
    }
}
