using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using SaaS.AccessControl;
using SaaS.Identity;
using SaaS.Orders;
using SaaS.OrderApprovals;
using SaaS.Web.Access;
namespace SaaS.Web.Orders;

public sealed class OrdersEndpoints : ITenantApplicationEndpoints
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8
    };
    private sealed record Route(bool Api, Guid? Id = null, OrderAction? Action = null, bool New = false);
    private sealed record EditBody(long ExpectedRevision, string ExpectedVersion, OrderDraft Draft);
    private sealed record SubmitBody(long ExpectedRevision, string ExpectedVersion);
    private sealed record RejectBody(long ExpectedRevision, string ExpectedVersion, string Reason);
    public bool Matches(string path, string method) => ParseRoute(path, method) is not null;
    private static Route? ParseRoute(string path, string method)
    {
        var api = path.StartsWith("/api/v1/orders", StringComparison.Ordinal);
        var root = api ? "/api/v1/orders" : "/orders";
        if (path == root)
        {
            if (HttpMethods.IsGet(method)) return new(api);
            if (api && HttpMethods.IsPost(method)) return new(true, Action: OrderAction.Create);
            return null;
        }
        if (!path.StartsWith(root + "/", StringComparison.Ordinal)) return null;
        var parts = path[(root.Length + 1)..].Split('/');
        if (!api && parts.Length == 1 && parts[0] == "new")
            return HttpMethods.IsGet(method) ? new(false, New: true) : HttpMethods.IsPost(method) ? new(false, Action: OrderAction.Create, New: true) : null;
        if (!Guid.TryParseExact(parts[0], "D", out var id) || id == Guid.Empty) return null;
        if (parts.Length == 1 && HttpMethods.IsGet(method)) return new(api, id);
        if (parts.Length != 2 || !HttpMethods.IsPost(method)) return null;
        return parts[1] switch { "edit" => new(api, id, OrderAction.Edit), "submit" => new(api, id, OrderAction.Submit), "approve" => new(api, id, OrderAction.Approve), "reject" => new(api, id, OrderAction.Reject), _ => null };
    }
    public async Task HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        try
        {
            var route = ParseRoute(context.Request.Path.Value ?? "", context.Request.Method) ?? throw new AccessDeniedException(404, "route_not_found");
            var authority = new ApplicationRequestAuthority(context);
            var initial = await authority.ReadAsync(ct); // Service rechecks current authority under its locks.
            if (route.Action is OrderAction.Approve or OrderAction.Reject && !initial.Current.Entitlements.Contains("Approval"))
                throw new AccessDeniedException(404, "route_not_found");
            var service = new OrderService(authority.Connection, authority.Tenant.TenantId, ApplicationAccessProfile.Catalog,
                async (tx, token) =>
                {
                    AuthenticatedApplicationAccess? identity = null;
                    var access = await TransactionAccessReader.ReadAsync(authority.Tenant.TenantId, tx, async currentToken =>
                    {
                        identity = await authority.ReadAsync(currentToken, tx);
                        return identity.Current;
                    }, token);
                    return new(access.Current, access.Snapshot, identity!.ClientId);
                }, new OrderApprovalHandler(context.RequestServices.GetRequiredService<TimeProvider>()), clock: context.RequestServices.GetRequiredService<TimeProvider>(), correlationId: context.TraceIdentifier);
            context.Response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsGet(context.Request.Method))
            {
                if (route.New)
                {
                    await service.DemandCreateAsync(ct);
                    await OrdersPages.NewAsync(context);
                }
                else if (route.Id is { } id)
                {
                    var order = await service.GetDetailAsync(id, ct);
                    if (route.Api) await context.Response.WriteAsJsonAsync(order, Json, ct);
                    else await OrdersPages.DetailAsync(context, order, order.Status == OrderStatus.PendingApproval && await service.CanDecideAsync(id, ct));
                }
                else
                {
                    var page = 0;
                    if (context.Request.Query.TryGetValue("page", out var values)
                        && (values.Count != 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out page)))
                        throw new AccessDeniedException(400, "page_invalid");
                    var result = await service.ListAsync(page, ct);
                    if (route.Api) await context.Response.WriteAsJsonAsync(result, Json, ct);
                    else await OrdersPages.ListAsync(context, result, page);
                }
                return;
            }
            var command = route.Api ? await ApiCommandAsync(context, route, ct) : await WebCommandAsync(context, route, ct);
            var committed = await service.ExecuteAsync(command, ct);
            if (route.Api)
            {
                context.Response.StatusCode = route.Action == OrderAction.Create ? 201 : 200;
                context.Response.Headers.Location = context.Request.PathBase + "/api/v1/orders/" + committed.Order.Id.ToString("D");
                await context.Response.WriteAsJsonAsync(committed, Json, ct);
            }
            else
            {
                context.Response.StatusCode = 303;
                context.Response.Headers.Location = context.Request.PathBase + "/orders/" + committed.Order.Id.ToString("D");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { context.Abort(); }
        catch (AccessDeniedException error) { await IdentityProtocol.WriteErrorAsync(context, error.StatusCode, error.Code); }
        catch (AntiforgeryValidationException) { await IdentityProtocol.WriteErrorAsync(context, 400, "antiforgery_invalid"); }
        catch (JsonException) { await IdentityProtocol.WriteErrorAsync(context, 400, "json_invalid"); }
        catch (InvalidDataException) { await IdentityProtocol.WriteErrorAsync(context, 400, "request_invalid"); }
        catch { await IdentityProtocol.WriteErrorAsync(context, 503, "order_dependency_unavailable"); }
    }
    private static async Task<OrderCommand> ApiCommandAsync(HttpContext context, Route route, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType()) throw Invalid();
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || keys[0] is null) throw Invalid();
        var bytes = await ReadBoundedAsync(context, ct);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        RejectDuplicates(document.RootElement);
        var root = document.RootElement;
        if (route.Action == OrderAction.Create)
        {
            Shape(root, "number", "customerName", "amount", "currency", "notes");
            return new(OrderAction.Create, keys[0]!, Draft: root.Deserialize<OrderDraft>(Json));
        }
        if (route.Action == OrderAction.Edit)
        {
            Shape(root, "expectedRevision", "expectedVersion", "draft");
            Shape(root.GetProperty("draft"), "number", "customerName", "amount", "currency", "notes");
            var body = root.Deserialize<EditBody>(Json) ?? throw Invalid();
            return new(OrderAction.Edit, keys[0]!, route.Id, body.ExpectedRevision, body.ExpectedVersion, body.Draft);
        }
        if (route.Action == OrderAction.Reject)
        {
            Shape(root, "expectedRevision", "expectedVersion", "reason");
            var body = root.Deserialize<RejectBody>(Json) ?? throw Invalid();
            return new(OrderAction.Reject, keys[0]!, route.Id, body.ExpectedRevision, body.ExpectedVersion, Reason: body.Reason);
        }
        Shape(root, "expectedRevision", "expectedVersion");
        var submit = root.Deserialize<SubmitBody>(Json) ?? throw Invalid();
        return new(route.Action!.Value, keys[0]!, route.Id, submit.ExpectedRevision, submit.ExpectedVersion);
    }
    private static async Task<OrderCommand> WebCommandAsync(HttpContext context, Route route, CancellationToken ct)
    {
        if (!context.Request.HasFormContentType) throw Invalid();
        var bytes = await ReadBoundedAsync(context, ct); // Also bounds chunked bodies before the form/antiforgery parser.
        var original = context.Request.Body;
        try
        {
            await using var body = new MemoryStream(bytes, writable: false);
            context.Request.Body = body;
            await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(ct);
            var allowed = new HashSet<string>(["__RequestVerificationToken", "idempotencyKey"], StringComparer.Ordinal);
            if (route.Action != OrderAction.Create) allowed.UnionWith(["expectedRevision", "expectedVersion"]);
            if (route.Action is OrderAction.Create or OrderAction.Edit) allowed.UnionWith(["number", "customerName", "amount", "currency", "notes"]);
            if (route.Action == OrderAction.Reject) allowed.Add("reason");
            if (form.Files.Count != 0 || form.Count != allowed.Count || form.Any(x => !allowed.Contains(x.Key) || x.Value.Count != 1)) throw Invalid();
            string Value(string key) => form[key].Count == 1 ? form[key].ToString() : throw Invalid();
            long? revision = null; string? version = null; OrderDraft? draft = null;
            if (route.Action != OrderAction.Create)
            {
                if (!long.TryParse(Value("expectedRevision"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) throw Invalid();
                revision = parsed; version = Value("expectedVersion");
            }
            if (route.Action is OrderAction.Create or OrderAction.Edit)
            {
                if (!decimal.TryParse(Value("amount"), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)) throw Invalid();
                draft = new(Value("number"), Value("customerName"), amount, Value("currency"), Value("notes"));
            }
            return new(route.Action!.Value, Value("idempotencyKey"), route.Id, revision, version, draft, route.Action == OrderAction.Reject ? Value("reason") : null);
        }
        finally { context.Request.Body = original; }
    }
    private static void Shape(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var actual = element.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(names)) throw Invalid();
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw Invalid(); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) RejectDuplicates(value);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContext context, CancellationToken ct)
    {
        const int limit = 32768;
        if (context.Request.ContentLength > limit) throw Invalid();
        using var buffer = new MemoryStream(); var chunk = new byte[4096]; int count;
        while ((count = await context.Request.Body.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            if (buffer.Length + count > limit) throw Invalid();
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        return buffer.ToArray();
    }
    private static AccessDeniedException Invalid() => new(400, "order_request_invalid");
}
