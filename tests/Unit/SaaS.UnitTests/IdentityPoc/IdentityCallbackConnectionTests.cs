using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M2IdentityPocTests
{
    [Theory]
    [InlineData("/connect/authorize", "redirect_uri")]
    [InlineData("/connect/logout", "post_logout_redirect_uri")]
    public async Task Callback_form_wait_does_not_retain_a_runtime_SQL_session(string path, string redirectParameter)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var http = await IdentityHttpFixture.CreateAsync(fixture.Environment, productHost: true);
        TestServer server = null!;
        await http.InDefaultAsync(provider =>
        {
            server = Assert.IsType<TestServer>(provider.GetRequiredService<IServer>());
            return Task.CompletedTask;
        });
        var form = new PausedCallbackForm(redirectParameter);
        var request = server.SendAsync(context =>
        {
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost");
            context.Request.Method = "POST";
            context.Request.Path = path;
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Features.Set<IFormFeature>(form);
        }, ct);
        HttpContext response = null!;
        try
        {
            await form.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await using var admin = await SqlOperations.OpenAsync(http.Environment.Admin, ct);
            var sessions = Convert.ToInt32(await SqlOperations.ScalarAsync(admin,
                "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1 AND login_name = N'saas_platform_runtime'", ct));
            Assert.Equal(0, sessions);
        }
        finally
        {
            form.Resume.TrySetResult();
            response = await request;
        }
        Assert.Equal(StatusCodes.Status400BadRequest, response.Response.StatusCode);
    }

    private sealed class PausedCallbackForm(string redirectParameter) : IFormFeature
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasFormContentType => true;
        public IFormCollection? Form { get; set; }
        public IFormCollection ReadForm() => throw new InvalidOperationException("The callback must read forms asynchronously.");
        public async Task<IFormCollection> ReadFormAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(ct);
            return Form ??= new FormCollection(new Dictionary<string, StringValues>
            {
                ["client_id"] = "absent-callback-fixture-client",
                [redirectParameter] = "https://localhost/alpha/callback-fixture"
            });
        }
    }
}
