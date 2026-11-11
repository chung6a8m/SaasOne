using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace SaaS.UnitTests;

/// <summary>
/// Test-only, closed-vocabulary attribution. No request values other than
/// an allowlisted tenant and operation are ever attached to the Activity.
/// Neither authentication nor business policy consumes this instrumentation.
/// </summary>
internal static class M7OperationAttribution
{
    private const string RootName = "m7.sanitized-request";
    private static readonly HashSet<string> Tenants = new(StringComparer.Ordinal)
        { "alpha", "beta", "gamma", "delta" };
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
        { "create", "edit", "submit", "approve", "detail", "list" };
    private static int _enabled;

    internal static IDisposable Enable()
    {
        Interlocked.Increment(ref _enabled);
        return new Activation();
    }

    internal static async Task Middleware(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (Volatile.Read(ref _enabled) == 0
            || !TryClassify(context.Request.Method, context.Request.Path.Value, out var tenant, out var operation))
        {
            await next(context);
            return;
        }

        // Only fixed vocabulary labels. Never attach the route's resource GUID,
        // user, request path, headers, token, query, cookie or correlation ID.
        using var root = new Activity(RootName)
            .SetTag("m7.tenant", tenant)
            .SetTag("m7.operation", operation)
            .Start();
        await next(context);
    }

    private static bool TryClassify(string method, string? path, out string tenant, out string operation)
    {
        tenant = operation = string.Empty;
        var segments = path?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is null || segments.Length is < 4 or > 6
            || !Tenants.Contains(segments[0])
            || segments[1] != "api" || segments[2] != "v1" || segments[3] != "orders")
            return false;

        var selected = string.Empty;
        if (segments.Length == 4)
            selected = method switch { "POST" => "create", "GET" => "list", _ => "" };
        else if (Guid.TryParseExact(segments[4], "D", out var resource) && resource != Guid.Empty)
        {
            if (segments.Length == 5 && method == "GET") selected = "detail";
            if (segments.Length == 6 && method == "POST"
                && segments[5] is "edit" or "submit" or "approve")
                selected = segments[5];
        }

        if (!Operations.Contains(selected)) return false;
        tenant = segments[0];
        operation = selected;
        return true;
    }

    /// <summary>
    /// Read ONLY verified, literal-vocabulary tags on the test-owned root.
    /// Ignore any unsanitized/spoofed tag, route path, or unrelated Activity.
    /// </summary>
    internal static (string Tenant, string Operation)? For(Activity activity)
    {
        for (Activity? current = activity; current is not null; current = current.Parent)
        {
            if (current.OperationName != RootName) continue;
            if (current.GetTagItem("m7.tenant") is string tenant
                && current.GetTagItem("m7.operation") is string operation
                && Tenants.Contains(tenant) && Operations.Contains(operation))
                return (tenant, operation);
            return null;
        }
        return null;
    }

    private sealed class Activation : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _enabled);
        }
    }
}
