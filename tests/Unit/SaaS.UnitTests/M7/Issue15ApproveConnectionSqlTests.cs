using System.Net;
using SaaS.Orders;
using Xunit;

namespace SaaS.UnitTests;

// Supplemental SQL/HTTPS POC, not an eighth mandatory M7 case.
// The unchanged M7 inventory must remain exactly seven cases.
[Collection("M7 SQL")]
[Trait("Category", "SqlIssue15")]
public sealed class Issue15ApproveConnectionSqlTests(M7SqlFixture fixture) : IClassFixture<M7SqlFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Approve_uses_eight_physical_logins_with_fresh_authority_and_one_owned_orders_transaction()
    {
        // Real Kestrel/HTTPS, native Orchard+YesSQL, EF, ACL and SQL Server.
        // Exclude setup, token issuance and Submit from this request-local count.
        const string slug = "beta";
        var creator = (await fixture.GrantAsync(fixture.Creators[slug][0], slug)).AccessToken;
        var order = await M7Http.CreateAsync(fixture, creator, slug, null, Ct);
        using (var submit = await M7Http.SendAsync(fixture, creator, slug,
            "/" + order.Id + "/submit", M7Http.Revision(order), ct: Ct))
            order = (await M7Http.ResultAsync(submit, HttpStatusCode.OK, Ct)).Order;
        Assert.Equal(OrderStatus.PendingApproval, order.Status);
        var reviewer = (await fixture.GrantAsync(fixture.Reviewers[0], slug)).AccessToken;

        using var stageObserver = new M7StageProfile();
        using var profile = new M7SqlProfile();
        using var response = await M7Http.SendAsync(fixture, reviewer, slug,
            "/" + order.Id + "/approve", M7Http.Revision(order), ct: Ct);
        var approved = await M7Http.ResultAsync(response, HttpStatusCode.OK, Ct);
        Assert.Equal(OrderStatus.Confirmed, approved.Order.Status);
        Assert.False(approved.Replayed);

        var observed = profile.Snapshot();
        // RED: 2 routing + 1 standalone customer profile + 1 Orders UoW
        // + 4 fresh full platform/native validations = 8; previously 12.
        Assert.Equal(8, observed.Open.Count);
        Assert.Equal(0, observed.Open.Errors);
        Assert.Equal(0, observed.Pending);
        var native = Assert.Single(profile.SnapshotStages(), x => x.Stage == "identity.native");
        var platform = Assert.Single(profile.SnapshotStages(), x => x.Stage == "identity.fresh");
        Assert.Equal(4, native.Open.Count);
        Assert.Equal(0, platform.Open.Count);
        Assert.True(native.Command.Count >= 4);
        Assert.True(platform.Command.Count >= 8,
            "Every validation must re-read platform authority and post-native tenant.");
        var stages = stageObserver.Snapshot().Stages;
        Assert.Contains(stages, stage => stage.Stage == "acl.authorize" && stage.Count >= 3);
    }

}
