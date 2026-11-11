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
    public void Orders_stage_timings_are_grouped_by_allowlisted_tenant_and_operation_without_request_secrets()
    {
        // A synthetic, harmless request root models the sanitized test-only
        // metadata without relying on a production route, SQL or credential.
        using var observer = new M7StageProfile();
        using var source = new ActivitySource("SaaS.RequestStages");
        var sensitive = "no-export-identifier-" + Guid.NewGuid().ToString("N");
        using (var request = new Activity("m7.sanitized-request")
            .SetTag("m7.tenant", "beta")
            .SetTag("m7.operation", "approve")
            .SetTag("m7.secret", sensitive).Start())
        {
            using (source.StartActivity("orders.write"))
            using (source.StartActivity("tenant.transaction"))
            using (var resource = source.StartActivity("orders.lock.resource"))
                resource?.AddEvent(new ActivityEvent("acquired"));
        }
        // Runtime RED: old report exports only aggregate Stages/Holds.
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
        var rows = document.RootElement.GetProperty("ByOperation").EnumerateArray().ToArray();
        Assert.Contains(rows, row =>
            row.GetProperty("Tenant").GetString() == "beta"
            && row.GetProperty("Operation").GetString() == "approve"
            && row.GetProperty("Stage").GetString() == "orders.lock.resource"
            && row.GetProperty("Count").GetInt32() == 1);
        Assert.Contains(rows, row =>
            row.GetProperty("Tenant").GetString() == "beta"
            && row.GetProperty("Operation").GetString() == "approve"
            && row.GetProperty("Stage").GetString() == "orders.hold.resource"
            && row.GetProperty("Count").GetInt32() == 1);
        Assert.DoesNotContain(sensitive, document.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_or_unsanitized_request_categories_never_enter_operation_profiles()
    {
        using var observer = new M7StageProfile();
        using var source = new ActivitySource("SaaS.RequestStages");
        using (new Activity("m7.sanitized-request")
            .SetTag("m7.tenant", "beta")
            .SetTag("m7.operation", "approve")
            .Start())
            using (source.StartActivity("orders.read")) { }
        using (new Activity("m7.sanitized-request")
            .SetTag("m7.tenant", "other-tenant")
            .SetTag("m7.operation", "approve")
            .Start())
            using (source.StartActivity("orders.read")) { }
        using (new Activity("m7.sanitized-request")
            .SetTag("m7.tenant", "beta")
            .SetTag("m7.operation", "approve?secret=invalid")
            .Start())
            using (source.StartActivity("orders.read")) { }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
        var grouped = document.RootElement.GetProperty("ByOperation").EnumerateArray().ToArray();
        Assert.Single(grouped);
        Assert.Equal("beta", grouped[0].GetProperty("Tenant").GetString());
        Assert.Equal("approve", grouped[0].GetProperty("Operation").GetString());
        Assert.Equal(1, grouped[0].GetProperty("Count").GetInt32());
        Assert.DoesNotContain("other-tenant", document.RootElement.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("invalid", document.RootElement.ToString(), StringComparison.Ordinal);
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
