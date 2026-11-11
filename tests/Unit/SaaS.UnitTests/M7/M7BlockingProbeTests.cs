using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M7BlockingProbeTests
{
    [Theory]
    [InlineData("other_database", "test")]
    [InlineData("saas_platform]; SELECT 1;--", "test")]
    [InlineData("saas_platform", "user@example.test")]
    [InlineData("saas_t_0000000000000000000000000000000g", "test")]
    public void Rejects_non_fixture_identifiers_and_non_allowlisted_labels(string database, string label)
    {
        using var sql = new SqlConnection();
        Assert.Throws<ArgumentException>(() => new M7BlockingProbe(sql, new Dictionary<string, string> { [database] = label }));
    }
}

public sealed partial class M1SqlTests
{
    [Fact]
    public async Task Blocking_probe_observes_a_real_wait_without_exporting_identifiers_or_closing_its_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = fixture.Environment.Tenants[0];
        var database = TenancyConnections.TenantDatabase(tenant.Id);
        await using var observer = await SqlOperations.OpenAsync(fixture.Environment.Admin, ct);
        var probe = new M7BlockingProbe(observer, new Dictionary<string, string> { [database] = "test" });
        await using var blocker = await SqlOperations.OpenAsync(fixture.Environment.CustomerAdmin(tenant), ct);
        var blockerId = Convert.ToInt32(await SqlOperations.ScalarAsync(blocker, "SELECT @@SPID", ct));
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(ct);
        await using (var command = SqlOperations.Command(blocker,
            "UPDATE [tenancy].[WriteGate] WITH (ROWLOCK) SET [Version] = [Version] + 1 WHERE [Id] = 1"))
        {
            command.Transaction = transaction;
            Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
        }
        await using var waiter = await SqlOperations.OpenAsync(fixture.Environment.Runtime(tenant), ct);
        var waiterId = Convert.ToInt32(await SqlOperations.ScalarAsync(waiter, "SELECT @@SPID", ct));
        var waiting = TenantWriteGate.ReadAsync(waiter, null, ct);
        try
        {
            var blocked = 0;
            var setup = Stopwatch.StartNew();
            do
            {
                blocked = Convert.ToInt32(await SqlOperations.ScalarAsync(observer,
                    "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @waiter AND blocking_session_id = @blocker AND wait_type LIKE 'LCK%';",
                    ct, ("@waiter", waiterId), ("@blocker", blockerId)));
                if (blocked > 0) break;
                await Task.Delay(50, ct);
            } while (setup.Elapsed < TimeSpan.FromSeconds(5));
            Assert.True(blocked > 0 && !waiting.IsCompleted, "Fixture must prove an actual blocked reader before evaluating the probe.");
            IReadOnlyList<M7BlockingRow> rows = [];
            var timer = Stopwatch.StartNew();
            do
            {
                rows = await probe.ReadAsync(ct);
                if (rows.Any(x => x.Resource == "tenancy.WriteGate" && x.TaskObservations > 0)) break;
                await Task.Delay(50, ct);
            } while (timer.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Contains(rows, x => x.Tenant == "test" && x.Resource == "tenancy.WriteGate" && x.TaskObservations > 0);
            var serialized = JsonSerializer.Serialize(rows);
            Assert.DoesNotContain(database, serialized);
            Assert.DoesNotContain(tenant.Id.ToString("N"), serialized);
            Assert.DoesNotContain("ConnectionString", serialized);
            Assert.DoesNotContain("CommandText", serialized);
            Assert.Equal(ConnectionState.Open, observer.State);
            Assert.False(waiting.IsCompleted, "The diagnostic must not release or bypass the held lock.");
        }
        finally { await transaction.RollbackAsync(CancellationToken.None); await waiting; }
        Assert.Equal(tenant.Id, (await waiting).TenantId);
        Assert.Equal(ConnectionState.Open, observer.State);
    }
}
