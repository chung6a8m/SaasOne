using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using SaaS.Identity;
using SaaS.Tenancy.Operations;
using Xunit;

namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Disabling_runtime_login_inside_one_borrowed_validation_must_fail_closed(int platformReadBoundary)
    {
        // The existing M2 tests only disable the runtime login BETWEEN validations.
        // Pause this real, unpooled YesSQL+EF validation immediately AFTER its
        // first platform source SELECT (1), or its post-native tenant SELECT (2).
        // SQL Server does not automatically terminate an already-open session.
        var ct = TestContext.Current.CancellationToken;
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment);
        using var browser = http.NewBrowser();
        await browser.LoginAsync(http);
        using (var response = await browser.FollowAsync("/alpha/account/login"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var token = http.CapturedTokens["alpha"].AccessToken;
        var tenantId = http.Tenant("alpha").Id;
        await http.InDefaultAsync(async provider =>
        {
            var request = new DefaultHttpContext { RequestServices = provider };
            request.Request.Headers.Authorization = "Bearer " + token;

            // The admin uses a SEPARATE privileged login; it never enters the
            // runtime validation, nor is it borrowed by EF or YesSQL.
            await using var admin = await SqlOperations.OpenAsync(http.Environment.PlatformAdmin, ct);
            var platformReads = 0;
            var disableAttempted = 0;
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == SaaSStageActivity.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.Source.Name != SaaSStageActivity.SourceName || activity.OperationName != "identity.fresh")
                        return;
                    if (Interlocked.Increment(ref platformReads) != platformReadBoundary) return;

                    // ActivityStopped runs synchronously at the exact boundary,
                    // before the application can proceed to native checks/return.
                    Interlocked.Exchange(ref disableAttempted, 1);
                    using var sql = SqlOperations.Command(admin, "ALTER LOGIN [saas_platform_runtime] DISABLE");
                    sql.ExecuteNonQuery();
                }
            };
            ActivitySource.AddActivityListener(listener);

            try
            {
                var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    IdentityProtocol.AuthenticateBearerForApplicationAsync(request, tenantId));
                Assert.Equal("Authoritative native SQL principal disabled or unavailable.", denied.Message);
                Assert.Equal(platformReadBoundary, Volatile.Read(ref platformReads));
                Assert.Equal(1, Volatile.Read(ref disableAttempted));
            }
            finally
            {
                listener.Dispose();
                if (Volatile.Read(ref disableAttempted) != 0)
                    await SqlOperations.ExecuteAsync(admin,
                        "ALTER LOGIN [saas_platform_runtime] ENABLE", CancellationToken.None);
            }

            // No cached denial or cached authority after the separate SQL login
            // is enabled: a NEW validation must succeed with a NEW native scope.
            var resumed = await IdentityProtocol.AuthenticateBearerForApplicationAsync(request, tenantId);
            Assert.Equal(tenantId, resumed.Family.TenantId);
        });
    }
}
