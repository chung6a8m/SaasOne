using System.Diagnostics;
using System.Text.Json;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M6 SQL")]
[Trait("Category", "SqlM6")]
public sealed class M6ProcessSqlTests(M1SqlFixture fixture)
{
    [Fact]
    public async Task Live_or_ambiguous_bundle_launch_blocks_takeover_even_before_child_opens_SQL()
    {
        var tenant = fixture.Environment.Tenants[0]; var resource = "tenant-" + tenant.Id.ToString("N");
        var marker = Path.Combine(TenancyConnections.Required("SAAS_OPERATIONS_HOME"), resource + ".child.json");
        using var process = Process.GetCurrentProcess();
        foreach (var live in new[] { false, true })
        {
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { Version = 1, Resource = resource,
                OperationId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), ProcessId = live ? (int?)process.Id : null,
                StartUtcTicks = live ? (long?)process.StartTime.ToUniversalTime().Ticks : null }), TestContext.Current.CancellationToken);
            try
            {
                var error = await Assert.ThrowsAsync<TenantContractException>(async () =>
                { await using var lease = await OperationLease.AcquireAsync(new TenancyConnections().Platform("operator"), resource, TestContext.Current.CancellationToken); });
                Assert.Equal(live ? "prior_owner_still_active" : "operation_child_reconciliation_required", error.Code);
                Assert.True(File.Exists(marker));
            }
            finally { File.Delete(marker); } // Fixture owns this marker and no child was launched.
        }
    }
}
