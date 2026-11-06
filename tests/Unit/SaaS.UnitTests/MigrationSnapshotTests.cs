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
        Assert.Single(assembly.Migrations);
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
