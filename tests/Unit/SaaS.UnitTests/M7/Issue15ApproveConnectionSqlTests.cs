using System.Net;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Orders;
using Xunit;

namespace SaaS.UnitTests;

// Supplemental SQL/HTTPS POC lives in the SAME fixture as the seven
// mandatory M7 tests, but has its own category and manifest check.
public sealed partial class M7SqlAcceptanceTests
{
    [Fact]
    [Trait("Category", "SqlIssue15")]
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

        // RED for the release+route single-login optimization. The real HTTPS
        // fixture currently performs one release-gate SQL open PLUS routing.
        // Every successful readiness request must still execute the release
        // check, platform bootstrap/schema/native and customer nonce checks.
        foreach (var (path, expected) in new[] { ("/health/ready", 1), ("/beta/health/ready", 2) })
        {
            using var warm = await fixture.Api.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
            using var readySql = new M7SqlProfile();
            using var ready = await fixture.Api.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            var result = readySql.Snapshot();
            Assert.True(result.Open.Count == expected,
                $"Fresh release+route readiness must use {expected} physical SQL opens; observed {result.Open.Count}.");
            Assert.True(result.Command.Count >= 8, "Release, bootstrap, schema, identity and native readiness must remain fresh.");
            Assert.Equal(0, result.Open.Errors);
            Assert.Equal(0, result.Pending);
        }

        // Preserve the release gate: even when the original middleware is
        // integrated into the routed SQL login, a pending offline release
        // must close every valid tenant API and both readiness endpoints.
        var releaseOperation = Guid.NewGuid();
        var completed = false;
        try
        {
            await using (var db = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin))
            {
                db.Audit.Add(new OperationalAudit
                {
                    OperationId = releaseOperation, Actor = "issue15-disposable-fixture",
                    Action = PlatformReleaseBarrier.Bound, OccurredAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync(Ct);
            }
            foreach (var path in new[] { "/health/ready", "/beta/health/ready" })
            {
                using var blocked = await fixture.Api.GetAsync(path, Ct);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            }
            using (var blocked = await M7Http.SendAsync(fixture, creator, slug,
                "/" + order.Id, ct: Ct))
                Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        }
        finally
        {
            await using var db = TenancyConnections.PlatformContext(fixture.Environment.PlatformAdmin);
            db.Audit.Add(new OperationalAudit
            {
                OperationId = releaseOperation, Actor = "issue15-disposable-fixture",
                Action = PlatformReleaseBarrier.Completed, OccurredAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(CancellationToken.None);
            completed = true;
        }
        Assert.True(completed);
        using (var ready = await fixture.Api.GetAsync("/beta/health/ready", Ct))
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

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
        Assert.Equal(0, observed.Open.Errors);
        Assert.Equal(0, observed.Pending);
        var stageSql = profile.SnapshotStages();
        var native = Assert.Single(stageSql, x => x.Stage == "identity.native");
        var platform = Assert.Single(stageSql, x => x.Stage == "identity.fresh");
        Assert.Equal(4, native.Open.Count);
        Assert.Equal(0, platform.Open.Count);
        Assert.Equal(2, Assert.Single(stageSql, x => x.Stage == "routing").Open.Count);
        Assert.Equal(1, Assert.Single(stageSql, x => x.Stage == "orders.write").Open.Count);
        Assert.Equal(1, Assert.Single(stageSql, x => x.Stage == "other").Open.Count);
        // Include every physical open (including the release-barrier gate),
        // rather than silently excluding unanticipated request middleware.
        var stageCounts = string.Join(", ", stageSql.OrderBy(x => x.Stage, StringComparer.Ordinal)
            .Select(x => x.Stage + "=" + x.Open.Count));
        Assert.True(observed.Open.Count == 8,
            $"Expected 8 physical SQL opens; observed {observed.Open.Count}; stage counts: {stageCounts}");
        Assert.True(native.Command.Count >= 4);
        Assert.True(platform.Command.Count >= 8,
            "Every validation must re-read platform authority and post-native tenant.");
        var stages = stageObserver.Snapshot().Stages;
        Assert.Contains(stages, stage => stage.Stage == "acl.authorize" && stage.Count >= 3);
    }

}
