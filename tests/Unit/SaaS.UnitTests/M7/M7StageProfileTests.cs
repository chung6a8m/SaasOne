using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SaaS.UnitTests;

public sealed class M7StageProfileTests
{
    [Fact]
    public void Activity_spans_record_stage_and_successful_resource_lock_hold_until_transaction_end()
    {
        using var profile = new M7StageProfile();
        using var source = new ActivitySource("SaaS.RequestStages");
        using (source.StartActivity("orders.write"))
        {
            using (source.StartActivity("tenant.transaction"))
            {
                using (var resource = source.StartActivity("orders.lock.resource"))
                    resource?.AddEvent(new ActivityEvent("acquired"));
            }
        }
        var result = profile.Snapshot();
        Assert.Contains(result.Stages, row => row.Stage == "orders.write" && row.Count == 1);
        Assert.Contains(result.Stages, row => row.Stage == "orders.lock.resource" && row.Count == 1);
        Assert.Contains(result.Holds, row => row.Stage == "orders.hold.resource" && row.Count == 1 && row.TotalMs >= 0);
        Assert.DoesNotContain("traceId", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Failed_lock_acquisition_never_creates_a_hold_sample()
    {
        using var profile = new M7StageProfile();
        using var source = new ActivitySource("SaaS.RequestStages");
        using (source.StartActivity("tenant.transaction"))
            using (source.StartActivity("orders.lock.command")) { }
        Assert.Empty(profile.Snapshot().Holds);
    }
}
