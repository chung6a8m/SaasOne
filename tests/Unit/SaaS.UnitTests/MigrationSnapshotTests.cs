using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SaaS.Tenancy.Data;
using Xunit;
namespace SaaS.UnitTests;

public sealed class MigrationSnapshotTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Each_owned_stream_has_a_retained_matching_snapshot(bool platform)
    {
        using DbContext context = platform ? new PlatformDesignTimeFactory().CreateDbContext([]) : new TenantDesignTimeFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.NotNull(assembly.ModelSnapshot);
        string[] expected = platform
            ? ["20261106135648_PlatformBaselineV1", "20261106180112_IdentityAuthority", "20261107023640_ResourceOperationsM3"]
            : ["20261106135652_TenantBaselineV1", "20261107015335_TenantWriteGateM3", "20261107050000_ApplicationProfileM3"];
        Assert.Equal(expected, assembly.Migrations.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
