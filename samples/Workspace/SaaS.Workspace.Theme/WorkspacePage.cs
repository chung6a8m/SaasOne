using System.Net;
namespace SaaS.Workspace.Theme;

public static class WorkspacePage
{
    public static string Render(string tenant)
    {
        var label = WebUtility.HtmlEncode(tenant);
        var ready = WebUtility.HtmlEncode("/" + Uri.EscapeDataString(tenant) + "/health/ready");
        return $"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Workspace</title><main><h1>Workspace</h1><p>Tenant: {label}</p><p>Independent platform foundation. No Orders or approvals application is installed.</p><a href=\"{ready}\">Readiness</a></main></html>";
    }
}
