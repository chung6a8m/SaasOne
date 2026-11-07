using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using SaaS.Orders;
namespace SaaS.Web.Orders;

/// <summary>Product-owned, script-free screens. All user content is plain text and HTML encoded.</summary>
internal static class OrdersPages
{
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Hidden(string name, string value) => "<input type=\"hidden\" name=\"" + name + "\" value=\"" + E(value) + "\">";
    private static string Csrf(HttpContext context) => Hidden("__RequestVerificationToken", context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken!);
    private static StringBuilder Start(HttpContext context, string title) => new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
        .Append(E(title)).Append(" · Orders</title></head><body><header><nav aria-label=\"Primary\"><a href=\"")
        .Append(E(context.Request.PathBase + "/orders")).Append("\">Orders</a> | <a href=\"")
        .Append(E(context.Request.PathBase + "/workspace")).Append("\">Workspace</a></nav></header><main><h1>").Append(E(title)).Append("</h1>");
    private static Task FinishAsync(HttpContext context, StringBuilder html)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(html.Append("</main></body></html>").ToString(), context.RequestAborted);
    }
    internal static Task NewAsync(HttpContext context)
    {
        var html = Start(context, "New order");
        html.Append("<p>Create a draft. Submission checks the current plan and your current permissions.</p><form method=\"post\" action=\"")
            .Append(E(context.Request.PathBase + "/orders/new")).Append("\">").Append(Csrf(context))
            .Append(Hidden("idempotencyKey", Guid.NewGuid().ToString("N")));
        Fields(html, new("", "", 0, "USD", ""));
        html.Append("<button type=\"submit\">Save draft</button></form>");
        return FinishAsync(context, html);
    }
    internal static Task ListAsync(HttpContext context, OrderPage page, int index)
    {
        var html = Start(context, "Orders");
        html.Append("<p><a href=\"").Append(E(context.Request.PathBase + "/orders/new")).Append("\">New order</a></p>");
        if (page.Items.Length == 0) html.Append("<p>No orders visible on this page.</p>");
        else
        {
            html.Append("<table><caption>Orders you can view</caption><thead><tr><th scope=\"col\">Number</th><th scope=\"col\">Customer</th><th scope=\"col\">Amount</th><th scope=\"col\">Status</th><th scope=\"col\">Revision</th></tr></thead><tbody>");
            foreach (var order in page.Items)
                html.Append("<tr><td><a href=\"").Append(E(context.Request.PathBase + "/orders/" + order.Id.ToString("D"))).Append("\">")
                    .Append(E(order.Number)).Append("</a></td><td>").Append(E(order.CustomerName)).Append("</td><td>")
                    .Append(order.Amount.ToString("0.00", CultureInfo.InvariantCulture)).Append(' ').Append(E(order.Currency)).Append("</td><td>")
                    .Append(order.Status).Append("</td><td>").Append(Number(order.Revision)).Append("</td></tr>");
            html.Append("</tbody></table>");
        }
        html.Append("<nav aria-label=\"Order pages\">");
        if (index > 0) html.Append("<a rel=\"prev\" href=\"").Append(E(context.Request.PathBase + "/orders?page=" + Number(index - 1))).Append("\">Previous</a> ");
        if (page.HasMore && index < 1000) html.Append("<a rel=\"next\" href=\"").Append(E(context.Request.PathBase + "/orders?page=" + Number(index + 1))).Append("\">Next</a>");
        return FinishAsync(context, html.Append("</nav>"));
    }
    internal static Task DetailAsync(HttpContext context, OrderView order, bool canDecide = false)
    {
        var html = Start(context, order.Number);
        html.Append("<dl><dt>Customer</dt><dd>").Append(E(order.CustomerName)).Append("</dd><dt>Amount</dt><dd>")
            .Append(order.Amount.ToString("0.00", CultureInfo.InvariantCulture)).Append(' ').Append(E(order.Currency))
            .Append("</dd><dt>Status</dt><dd>").Append(order.Status).Append("</dd><dt>Revision</dt><dd>")
            .Append(Number(order.Revision)).Append("</dd><dt>Approval obligation</dt><dd>")
            .Append(order.ApprovalRequired ? "Recorded; retained in history" : "Not recorded").Append("</dd></dl><h2>Notes</h2><pre>")
            .Append(E(order.Notes)).Append("</pre>");
        if (order.ApprovalDecisions is { Length: > 0 } decisions)
        {
            html.Append("<h2>Approval decisions</h2><ol>");
            foreach (var decision in decisions)
            {
                html.Append("<li>Submitted revision ").Append(Number(decision.SubmittedRevision))
                    .Append(", decision revision ").Append(Number(decision.ResultRevision))
                    .Append(": ").Append(decision.Approved ? "Approved" : "Rejected");
                if (!decision.Approved)
                    html.Append("<p>Reason for rejection:</p><pre>").Append(E(decision.Reason)).Append("</pre>");
                html.Append("</li>");
            }
            html.Append("</ol>");
        }
        if (order.Status is OrderStatus.Draft or OrderStatus.Rejected)
        {
            var csrf = Csrf(context);
            var expected = Hidden("expectedRevision", Number(order.Revision)) + Hidden("expectedVersion", order.Version);
            var path = context.Request.PathBase + "/orders/" + order.Id.ToString("D");
            html.Append("<h2>Edit draft</h2><form method=\"post\" action=\"").Append(E(path + "/edit")).Append("\">")
                .Append(csrf).Append(expected).Append(Hidden("idempotencyKey", Guid.NewGuid().ToString("N")));
            Fields(html, new(order.Number, order.CustomerName, order.Amount, order.Currency, order.Notes));
            html.Append("<button type=\"submit\">Save draft</button></form>");
            if (order.Status == OrderStatus.Draft)
                html.Append("<h2>Submit</h2><p>Submitting checks the current revision and approval requirements.</p><form method=\"post\" action=\"")
                    .Append(E(path + "/submit")).Append("\">").Append(csrf).Append(expected)
                    .Append(Hidden("idempotencyKey", Guid.NewGuid().ToString("N"))).Append("<button type=\"submit\">Submit order</button></form>");
            else html.Append("<p>Save the revised draft before submitting it again. The approval obligation is retained.</p>");
        }
        else html.Append("<p>This revision cannot be edited or submitted again.</p>");
        if (order.Status == OrderStatus.PendingApproval && canDecide)
        {
            var csrf = Csrf(context);
            var expected = Hidden("expectedRevision", Number(order.Revision)) + Hidden("expectedVersion", order.Version);
            var path = context.Request.PathBase + "/orders/" + order.Id.ToString("D");
            html.Append("<h2>Decision</h2><p>A decision applies only to this submitted revision.</p><form method=\"post\" action=\"")
                .Append(E(path + "/approve")).Append("\">").Append(csrf).Append(expected)
                .Append(Hidden("idempotencyKey", Guid.NewGuid().ToString("N"))).Append("<button type=\"submit\">Approve order</button></form>")
                .Append("<form method=\"post\" action=\"").Append(E(path + "/reject")).Append("\">").Append(csrf).Append(expected)
                .Append(Hidden("idempotencyKey", Guid.NewGuid().ToString("N")))
                .Append("<label for=\"reason\">Reason for rejection (plain text)</label><textarea id=\"reason\" name=\"reason\" maxlength=\"1000\" required></textarea><button type=\"submit\">Reject order</button></form>");
        }
        return FinishAsync(context, html);
    }
    private static void Fields(StringBuilder html, OrderDraft draft)
    {
        void Input(string name, string label, string value, string attributes) => html.Append("<p><label for=\"").Append(name).Append("\">")
            .Append(label).Append("</label><input id=\"").Append(name).Append("\" name=\"").Append(name).Append("\" value=\"")
            .Append(E(value)).Append("\" ").Append(attributes).Append("></p>");
        Input("number", "Number", draft.Number, "maxlength=\"64\" required");
        Input("customerName", "Customer", draft.CustomerName, "maxlength=\"160\" required");
        Input("amount", "Amount", draft.Amount.ToString("0.00", CultureInfo.InvariantCulture), "type=\"number\" min=\"0\" max=\"9999999999999999.99\" step=\"0.01\" required");
        Input("currency", "Currency (three letters)", draft.Currency, "maxlength=\"3\" minlength=\"3\" required");
        html.Append("<p><label for=\"notes\">Notes (plain text)</label><textarea id=\"notes\" name=\"notes\" rows=\"6\" maxlength=\"2000\">").Append(E(draft.Notes)).Append("</textarea></p>");
    }
}
