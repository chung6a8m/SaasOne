using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaaS.AccessControl;
namespace SaaS.Orders;

public static class OrderPolicy
{
    public static OrderCommand Normalize(OrderCommand command)
    {
        if (command is null || !Enum.IsDefined(command.Action)) throw Invalid();
        ValidateKey(command.Key);
        var create = command.Action == OrderAction.Create;
        string? version = null;
        if (create)
        {
            if (command.OrderId is not null || command.ExpectedRevision is not null || command.ExpectedVersion is not null) throw Invalid();
        }
        else
        {
            if (command.OrderId is null || command.OrderId == Guid.Empty || command.ExpectedRevision is null or < 1) throw Invalid();
            try
            {
                var bytes = Convert.FromBase64String(command.ExpectedVersion ?? "");
                if (bytes.Length != 8) throw Invalid();
                version = Convert.ToBase64String(bytes);
            }
            catch (FormatException) { throw Invalid(); }
        }
        OrderDraft? draft = null;
        if (command.Action is OrderAction.Create or OrderAction.Edit)
        {
            var input = command.Draft ?? throw Invalid();
            var number = Text(input.Number, 64).ToUpperInvariant();
            var currency = Text(input.Currency, 3).ToUpperInvariant();
            if (number.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '_' and not '.')
                || currency.Length != 3 || currency.Any(x => !char.IsAsciiLetter(x))
                || input.Amount < 0 || input.Amount > 9999999999999999.99m || decimal.Round(input.Amount, 2) != input.Amount) throw Invalid();
            draft = new(number, Text(input.CustomerName, 160), input.Amount, currency, Text(input.Notes, 2000, true, true));
        }
        else if (command.Draft is not null) throw Invalid();
        var reason = command.Action == OrderAction.Reject ? Text(command.Reason, 1000, multiline: true) : null;
        if (command.Action != OrderAction.Reject && command.Reason is not null) throw Invalid();
        return command with { ExpectedVersion = version, Draft = draft, Reason = reason };
    }

    public static string Fingerprint(OrderCommand command)
    {
        var value = Normalize(command);
        return Hash(JsonSerializer.Serialize(new
        {
            Version = 1, Action = (int)value.Action, value.OrderId, value.ExpectedRevision, value.ExpectedVersion,
            Draft = value.Draft is null ? null : new[] { value.Draft.Number, value.Draft.CustomerName,
                value.Draft.Amount.ToString("0.00", CultureInfo.InvariantCulture), value.Draft.Currency, value.Draft.Notes }, value.Reason
        }));
    }
    public static string Partition(Guid tenant, Guid actor, string client, OrderAction action, string key)
    {
        ValidateKey(key);
        if (tenant == Guid.Empty || actor == Guid.Empty || string.IsNullOrWhiteSpace(client) || client.Length > 200
            || client.Any(char.IsControl) || !Enum.IsDefined(action)) throw new AccessDeniedException(401, "credential_invalid");
        return Hash(JsonSerializer.Serialize(new { Version = 1, Tenant = tenant, Actor = actor, Client = client, Action = (int)action, Key = key }));
    }
    public static OrderTransition Next(OrderView current, OrderCommand command, bool approvalEntitled, bool handlerReady)
    {
        var request = Normalize(command);
        DemandIntegrity(current);
        if (request.OrderId != current.Id || request.ExpectedRevision != current.Revision || request.ExpectedVersion != current.Version)
            throw new AccessDeniedException(409, "order_revision_changed");
        if (current.Revision == long.MaxValue) throw new AccessDeniedException(503, "order_revision_exhausted");
        var revision = current.Revision + 1;
        if (request.Action == OrderAction.Edit && current.Status is OrderStatus.Draft or OrderStatus.Rejected)
            return new(OrderStatus.Draft, revision, current.ApprovalRequired);
        if (request.Action == OrderAction.Submit && current.Status == OrderStatus.Draft)
        {
            if (!current.ApprovalRequired && !approvalEntitled) return new(OrderStatus.Confirmed, revision, false);
            DemandApproval(approvalEntitled, handlerReady);
            return new(OrderStatus.PendingApproval, revision, true);
        }
        if (request.Action is OrderAction.Approve or OrderAction.Reject && current.Status == OrderStatus.PendingApproval)
        {
            DemandApproval(approvalEntitled, handlerReady);
            return new(request.Action == OrderAction.Approve ? OrderStatus.Confirmed : OrderStatus.Rejected, revision, true);
        }
        throw new AccessDeniedException(409, "order_transition_invalid");
    }
    public static void DemandIntegrity(OrderView value)
    {
        if (value.Id == Guid.Empty || value.CreatedBy == Guid.Empty || value.Revision < 1 || !Enum.IsDefined(value.Status)
            || value.Status is OrderStatus.PendingApproval or OrderStatus.Rejected && !value.ApprovalRequired)
            throw new AccessDeniedException(503, "order_state_invalid");
        try { if (Convert.FromBase64String(value.Version).Length != 8) throw new FormatException(); }
        catch (Exception error) when (error is FormatException or ArgumentNullException) { throw new AccessDeniedException(503, "order_state_invalid"); }
    }
    private static void DemandApproval(bool entitled, bool ready)
    {
        if (!entitled) throw new AccessDeniedException(403, "approval_entitlement_required");
        if (!ready) throw new AccessDeniedException(503, "approval_handler_unavailable");
    }
    private static void ValidateKey(string key)
    {
        if (key is null || key.Length is < 8 or > 128 || key.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '_' and not '.' and not ':')) throw Invalid();
    }
    private static string Text(string? value, int max, bool empty = false, bool multiline = false)
    {
        if (value is null) throw Invalid();
        string normalized;
        try { normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Normalize(NormalizationForm.FormC).Trim(); }
        catch (ArgumentException) { throw Invalid(); }
        if ((!empty && normalized.Length == 0) || normalized.Length > max
            || normalized.Any(x => char.IsControl(x) && !(multiline && x is '\n' or '\t'))) throw Invalid();
        return normalized;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static AccessDeniedException Invalid() => new(400, "order_command_invalid");
}
