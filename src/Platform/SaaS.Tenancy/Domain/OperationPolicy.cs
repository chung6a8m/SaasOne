using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Domain;

public static class OperationPolicy
{
    public static void DemandOwner(TenantStatus current, OperationStamp stamp, OperationStatus kind)
    {
        if (current.TenantId == Guid.Empty || kind is OperationStatus.None or OperationStatus.Failed || !Enum.IsDefined(kind)
            || current.Operation != kind || stamp.OperationId == Guid.Empty || stamp.OwnerId == Guid.Empty
            || current.OperationId != stamp.OperationId || current.OwnerId != stamp.OwnerId
            || stamp.Fence < 1 || current.Fence != stamp.Fence || stamp.RegistryVersion < 1 || current.RegistryVersion != stamp.RegistryVersion)
            throw new TenantContractException("operation_reconciliation_required");
    }
    public static TenantStatus Complete(TenantStatus current, OperationStamp stamp, OperationStatus kind, DateTimeOffset now, bool ready)
    {
        DemandOwner(current, stamp, kind);
        if (!ready || current.Subscription != SubscriptionStatus.Active || current.ExpiresAt <= now || current.SecurityRevalidationRequired)
            throw new TenantContractException("operation_reconciliation_required");
        return current with { Operation = OperationStatus.None, OperationId = null, OwnerId = null, RegistryVersion = checked(current.RegistryVersion + 1) };
    }
    public static string SelectPlan(string source, string target, ObligationResult obligations)
    {
        if (source is not ("Standard" or "Approval") || target is not ("Standard" or "Approval"))
            throw new TenantContractException("invalid_plan");
        if (obligations is null || obligations.State == ObligationState.Unknown || !Enum.IsDefined(obligations.State)
            || obligations.State == ObligationState.Clear && obligations.Count != 0
            || obligations.State == ObligationState.Outstanding && obligations.Count < 1)
            throw new TenantContractException("obligations_unknown");
        return target == "Standard" && obligations.State == ObligationState.Outstanding ? source : target;
    }
}
