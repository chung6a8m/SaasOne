using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
namespace SaaS.Workspace.Web.Release;
public static class PlatformReleaseRuntimeGate
{
    public static async Task EnterAsync(HttpContext context, RequestDelegate next)
    {
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/health/live") { await next(context); return; }
        try
        {
            await using var sql = await SqlOperations.OpenAsync(new TenancyConnections().Platform("runtime"), context.RequestAborted);
            await PlatformReleaseBarrier.DemandClearAsync(sql, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
        catch
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { status = "not-ready" }, context.RequestAborted);
            return;
        }
        await next(context);
    }
}
