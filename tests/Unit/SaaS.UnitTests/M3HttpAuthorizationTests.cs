using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SaaS.AccessControl;
using SaaS.AccessControl.Data;
using SaaS.Auditing;
using SaaS.Tenancy.Operations;
using Xunit;
namespace SaaS.UnitTests;

public sealed partial class M3RuntimeTests
{
    [Fact]
    public async Task M3_http_defaults_to_deny_and_rechecks_UI_API_allow_deny_overrides_without_cache()
    {
        await using var setup = await M3HttpAsync(grant: false);
        var http = setup.Http;
        using var browser = http.NewBrowser();
        await HttpLoginAsync(http, browser);
        var token = http.CapturedTokens["alpha"].AccessToken;
        await AssertHttpPairAsync(http, browser, token, HttpStatusCode.Forbidden);
        var allow = ManagerAcl(http.UserId);
        await SeedHttpAclAsync(Alpha, allow);
        await AssertHttpPairAsync(http, browser, token, HttpStatusCode.OK);
        var denial = Guid.NewGuid();
        var deny = new AclDocument([.. allow.Roles, new(denial, "Explicit veto", [new("Workspace.View", PermissionEffect.Deny)])],
            [.. allow.Assignments, new(http.UserId, denial)], [new(http.UserId, "Workspace.View", PermissionEffect.Allow)]);
        await SeedHttpAclAsync(Alpha, deny);
        await AssertHttpPairAsync(http, browser, token, HttpStatusCode.Forbidden);
        await SeedHttpAclAsync(Alpha, allow with { Overrides = [new(http.UserId, "Workspace.View", PermissionEffect.Unspecified)] });
        await AssertHttpPairAsync(http, browser, token, HttpStatusCode.OK);
        await SeedHttpAclAsync(Alpha, allow with { Overrides = [new(http.UserId, "Workspace.View", PermissionEffect.Deny)] });
        await AssertHttpPairAsync(http, browser, token, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task M3_http_role_commands_are_audited_idempotent_and_cannot_escalate_authority()
    {
        await using var setup = await M3HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await HttpLoginAsync(http, browser);
        var token = http.CapturedTokens["alpha"].AccessToken;
        var view = await HttpAclAsync(http, token);
        var own = view.Snapshot.Document with { Overrides = [new(http.UserId, "Workspace.View", PermissionEffect.Allow)] };
        using (var result = await HttpChangeAsync(http, token, new(Guid.NewGuid(), view.Snapshot.Revision, view.GateVersion, own))) Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        var target = await HttpTargetMemberAsync();
        var role = Guid.NewGuid();
        AclDocument With(string permission) => new([.. view.Snapshot.Document.Roles, new(role, "Reader", [new(permission, PermissionEffect.Allow)])],
            [.. view.Snapshot.Document.Assignments, new(target, role)], []);
        using (var result = await HttpChangeAsync(http, token, new(Guid.NewGuid(), view.Snapshot.Revision, view.GateVersion, With("Access.Manage")))) Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        using (var result = await HttpChangeAsync(http, token, new(Guid.NewGuid(), view.Snapshot.Revision, view.GateVersion, With("Unregistered.Power")))) Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        var change = new AclChange(Guid.NewGuid(), view.Snapshot.Revision, view.GateVersion, With("Workspace.View"));
        using (var result = await HttpChangeAsync(http, token, change)) Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        using (var replay = await HttpChangeAsync(http, token, change)) Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (var conflict = await HttpChangeAsync(http, token, change with { Document = view.Snapshot.Document })) Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var audit = AuditDbContext.Open(Environment.CustomerAdmin(Alpha));
        Assert.Equal(1, await audit.Events.CountAsync(x => x.Id == change.OperationId && x.Action == "AclChanged" && x.ActorId == http.UserId, Ct));
        await SeedHttpAclAsync(Alpha, change.Document with { Overrides = [new(http.UserId, "Access.Manage", PermissionEffect.Deny)] });
        using var revokedReplay = await HttpChangeAsync(http, token, change);
        Assert.Equal(HttpStatusCode.Forbidden, revokedReplay.StatusCode);
    }

    [Fact]
    public async Task M3_http_web_role_editor_requires_csrf_and_uses_shared_API_revision_checks()
    {
        await using var setup = await M3HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await HttpLoginAsync(http, browser);
        var token = http.CapturedTokens["alpha"].AccessToken;
        var view = await HttpAclAsync(http, token);
        var role = Guid.NewGuid();
        var document = view.Snapshot.Document with { Roles = [.. view.Snapshot.Document.Roles, new(role, "<script>alert(1)</script>", [new("Workspace.View", PermissionEffect.Allow)])] };
        var fields = new Dictionary<string, string>
        {
            ["operationId"] = Guid.NewGuid().ToString("D"), ["revision"] = view.Snapshot.Revision.ToString(CultureInfo.InvariantCulture),
            ["gateVersion"] = view.GateVersion.ToString(CultureInfo.InvariantCulture), ["document"] = document.CanonicalJson()
        };
        using (var noCsrf = await browser.PostAsync("/alpha/access", fields)) Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using (var form = await browser.GetAsync("/alpha/access"))
        {
            Assert.Equal(HttpStatusCode.OK, form.StatusCode);
            fields["__RequestVerificationToken"] = HttpBrowser.Csrf(await form.Content.ReadAsStringAsync(Ct));
        }
        using (var changed = await browser.PostAsync("/alpha/access", fields))
        {
            Assert.Equal(HttpStatusCode.SeeOther, changed.StatusCode);
            Assert.Equal("/alpha/access", changed.Headers.Location?.OriginalString);
        }
        Assert.Equal(view.Snapshot.Revision + 1, (await HttpAclAsync(http, token)).Snapshot.Revision);
        using (var form = await browser.GetAsync("/alpha/access"))
        {
            var html = await form.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("<script>alert(1)</script>", html);
            Assert.Contains("&lt;script&gt;", html);
        }
        fields["operationId"] = Guid.NewGuid().ToString("D");
        using var stale = await browser.PostAsync("/alpha/access", fields);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task M3_http_required_sources_fail_closed_with_correlation_without_reusing_prior_allow()
    {
        await using var setup = await M3HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await HttpLoginAsync(http, browser);
        var token = http.CapturedTokens["alpha"].AccessToken;
        foreach (var source in new[] { "issuer", "platform", "account", "membership", "acl", "profile" })
        {
            await AssertHttpPairAsync(http, browser, token, HttpStatusCode.OK);
            await SetHttpFaultAsync(http, source, true);
            try
            {
                using var api = await HttpProbeAsync(http, token);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);
                using var json = JsonDocument.Parse(await api.Content.ReadAsStringAsync(Ct));
                Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("correlationId").GetString()));
                using var web = await browser.GetAsync("/alpha/workspace");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, web.StatusCode);
                Assert.Null(web.Headers.Location);
            }
            finally { await SetHttpFaultAsync(http, source, false); }
        }
        await AssertHttpPairAsync(http, browser, token, HttpStatusCode.OK);
    }

    [Fact]
    public async Task M3_http_shell_and_resource_policy_ignore_client_tenant_and_owner_hints()
    {
        await using var setup = await M3HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await HttpLoginAsync(http, browser);
        var token = http.CapturedTokens["alpha"].AccessToken;
        using (var wrongTenant = await HttpProbeAsync(http, token, "/beta/api/v1/workspace")) Assert.Equal(HttpStatusCode.Unauthorized, wrongTenant.StatusCode);
        var target = await HttpTargetMemberAsync();
        using (var own = await HttpProbeAsync(http, token, "/alpha/api/v1/profile/" + http.UserId)) Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using (var other = await HttpProbeAsync(http, token, "/alpha/api/v1/profile/" + target + "?ownerId=" + http.UserId)) Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        using (var otherWeb = await browser.GetAsync("/alpha/profile/" + target)) Assert.Equal(HttpStatusCode.Forbidden, otherWeb.StatusCode);
        using var client = http.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alpha/api/v1/workspace?tenantId=" + http.Tenant("beta").Id);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Id", http.Tenant("beta").Id.ToString("D")); request.Headers.Add("X-Owner-Id", target.ToString("D"));
        using var routed = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, routed.StatusCode);
        using var content = JsonDocument.Parse(await routed.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Alpha.Id, content.RootElement.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task M3_http_audit_failure_rolls_back_role_state_and_retry_commits_exactly_once()
    {
        await using var setup = await M3HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await HttpLoginAsync(http, browser);
        var token = http.CapturedTokens["alpha"].AccessToken;
        var view = await HttpAclAsync(http, token);
        var role = Guid.NewGuid();
        var change = new AclChange(Guid.NewGuid(), view.Snapshot.Revision, view.GateVersion,
            view.Snapshot.Document with { Roles = [.. view.Snapshot.Document.Roles, new(role, "Audited reader", [new("Workspace.View", PermissionEffect.Allow)])] });
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        var user = "saas_rt_" + Alpha.Id.ToString("N");
        await SqlOperations.ExecuteAsync(sql, $"DENY INSERT ON audit.Events TO [{user}]", Ct);
        try
        {
            using var failed = await HttpChangeAsync(http, token, change);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            await using var db = AccessDbContext.Open(Environment.CustomerAdmin(Alpha));
            Assert.Equal(view.Snapshot.Revision, (await db.State.SingleAsync(Ct)).Revision);
            Assert.False(await db.Roles.AnyAsync(x => x.Id == role, Ct));
            await using var audit = AuditDbContext.Open(Environment.CustomerAdmin(Alpha));
            Assert.False(await audit.Events.AnyAsync(x => x.Id == change.OperationId, Ct));
        }
        finally { await SqlOperations.ExecuteAsync(sql, $"GRANT INSERT ON audit.Events TO [{user}]", Ct); }
        using var retry = await HttpChangeAsync(http, token, change);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task M3_http_application_roles_never_grant_CMS_operator_or_database_administration()
    {
        await using var setup = await M3HttpAsync(); var http = setup.Http;
        using var browser = http.NewBrowser(); await HttpLoginAsync(http, browser);
        foreach (var path in new[] { "/alpha/admin", "/alpha/api/features", "/alpha/Setup", "/alpha/media/upload", "/alpha/operator/tenants", "/operator/tenants", "/api/v1/tenants" })
        {
            using var get = await browser.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            using var post = await browser.PostAsync(path, new Dictionary<string, string>()); Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        }
        await using var sql = await SqlOperations.OpenAsync(Environment.Runtime(Alpha), Ct);
        foreach (var command in new[] { "CREATE TABLE dbo.M3RuntimeEscalation (Id int)", "DELETE FROM audit.Events", "UPDATE tenancy.WriteGate SET IsOpen=1",
            "DELETE FROM access.__EFMigrationsHistory", "DELETE FROM access.History", "SELECT * FROM saas_platform.platform.Tenants" })
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => SqlOperations.ExecuteAsync(sql, command, Ct));
    }

    private static async Task HttpLoginAsync(IdentityHttpFixture http, HttpBrowser browser)
    {
        await browser.LoginAsync(http);
        foreach (var slug in new[] { "alpha", "beta" })
            using (var response = await browser.FollowAsync("/" + slug + "/account/login")) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    private static async Task<HttpResponseMessage> HttpProbeAsync(IdentityHttpFixture http, string token, string path = "/alpha/api/v1/workspace")
    {
        using var client = http.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, Ct);
    }
    private static async Task<HttpResponseMessage> HttpChangeAsync(IdentityHttpFixture http, string token, AclChange change)
    {
        using var client = http.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/alpha/api/v1/access") { Content = JsonContent.Create(change) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, Ct);
    }
    private static async Task<AclView> HttpAclAsync(IdentityHttpFixture http, string token)
    {
        using var result = await HttpProbeAsync(http, token, "/alpha/api/v1/access");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        return (await result.Content.ReadFromJsonAsync<AclView>(Ct))!;
    }
    private static async Task AssertHttpPairAsync(IdentityHttpFixture http, HttpBrowser browser, string token, HttpStatusCode status)
    {
        using var api = await HttpProbeAsync(http, token); Assert.Equal(status, api.StatusCode);
        using var web = await browser.GetAsync("/alpha/workspace"); Assert.Equal(status, web.StatusCode);
    }
    private async Task SetHttpFaultAsync(IdentityHttpFixture http, string source, bool failed)
    {
        if (source is not ("acl" or "profile")) { await http.SetReadFailureAsync(source, failed); return; }
        await using var sql = await SqlOperations.OpenAsync(Environment.CustomerAdmin(Alpha), Ct);
        var table = source == "acl" ? "[access].[Roles]" : "[tenancy].[ApplicationProfile]";
        await SqlOperations.ExecuteAsync(sql, $"{(failed ? "DENY" : "GRANT")} SELECT ON {table} TO [saas_rt_{Alpha.Id:N}]", Ct);
    }
}
