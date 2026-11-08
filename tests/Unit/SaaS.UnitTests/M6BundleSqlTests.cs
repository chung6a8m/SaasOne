using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.OrderApprovals.Data;
using SaaS.Orders.Data;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

[Collection("M6 SQL")]
[Trait("Category", "SqlM6")]
public sealed class M6BundleSqlTests(M1SqlFixture fixture)
{
    [Fact]
    public async Task Every_real_bundle_handles_empty_previous_baseline_and_idempotent_reexecution()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var package = await M6ReleaseSqlTests.PackageAsync();
        foreach (var stream in package.Manifest.Streams)
        foreach (var previous in new[] { false, true })
        {
            var name = "saas_m6_bundle_" + Guid.NewGuid().ToString("N");
            var connection = new SqlConnectionStringBuilder(fixture.Environment.Admin) { InitialCatalog = name, Pooling = false }.ConnectionString;
            await using var server = await SqlOperations.OpenAsync(fixture.Environment.Admin, ct);
            await SqlOperations.ExecuteAsync(server, "DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC(@sql);", ct, ("@name", name));
            try
            {
                await using var db = Open(stream.Id, connection);
                if (previous) await db.GetService<IMigrator>().MigrateAsync(stream.Migrations.Length > 1 ? stream.Migrations[^2] : stream.Migrations[0], ct);
                var operation = Guid.NewGuid(); var owner = Guid.NewGuid();
                var tagged = TenancyConnections.ForOperation(connection, operation, owner);
                var resource = "tenant-" + fixture.Environment.Tenants[0].Id.ToString("N");
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    await OperationChildProcess.RunAsync(package.FilePath(stream.Bundle), Path.GetDirectoryName(package.FilePath("host/SaaS.Web.dll"))!, tagged, resource, operation, owner, ct);
                    await TenancyConnections.VerifySchemaAsync(db, ct);
                    Assert.Equal(stream.Migrations, (await db.Database.GetAppliedMigrationsAsync(ct)).Order(StringComparer.Ordinal).ToArray());
                }
            }
            finally
            {
                await SqlOperations.ExecuteAsync(server, "DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name); EXEC(@sql);", CancellationToken.None, ("@name", name));
            }
        }
    }
    private static DbContext Open(string id, string connection) => id switch
    {
        "platform" => TenancyConnections.PlatformContext(connection), "tenancy" => TenancyConnections.CustomerContext(connection),
        "access" => AccessDbContext.Open(connection), "audit" => AuditDbContext.Open(connection),
        "orders" => OrdersDbContext.Open(connection), "approvals" => ApprovalsDbContext.Open(connection), _ => throw new InvalidOperationException()
    };
}
