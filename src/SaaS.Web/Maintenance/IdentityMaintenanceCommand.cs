using System.Globalization;
using SaaS.Abstractions.Tenancy;
using SaaS.Identity;
using SaaS.Tenancy.Data;
using SaaS.Web.Orchard;

namespace SaaS.Web.Maintenance;

/// <summary>Invoked only before building the HTTP host. No password or client secret is accepted in argv.</summary>
internal static class IdentityMaintenanceCommand
{
    public static async Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        var connections = new TenancyConnections();
        var operations = new IdentityOperations(connections);
        switch (args[0])
        {
            case "identity-init" when args.Length == 2:
                await new IdentityInstaller(connections, IdentityDeployment.Load()).InitializeAsync(Id(args[1]), ct);
                return;
            case "identity-verify" when args.Length == 1:
                await IdentityInstaller.VerifyAsync(connections, IdentityDeployment.Load(), ct);
                return;
            case "identity-client" when args.Length == 3:
                await new IdentityInstaller(connections, IdentityDeployment.Load()).RegisterTenantAsync(Id(args[1]), Id(args[2]), ct);
                return;
            case "identity-enroll" when args.Length == 5:
                var tenants = args[4].Split(',').Select(Id).ToArray();
                if (tenants.Length == 0 || tenants.Distinct().Count() != tenants.Length) throw Invalid();
                var userId = await new IdentityInstaller(connections, IdentityDeployment.Load()).EnrollUserAsync(Id(args[1]), args[2], args[3],
                    TenancyConnections.Required("SAAS_IDENTITY_ENROLLMENT_PASSWORD"), tenants, ct);
                Console.WriteLine("global_user_id:" + userId.ToString("D"));
                return;
            case "identity-membership" when args.Length == 4:
                await operations.SetMembershipAsync(Id(args[1]), Id(args[2]), State(args[3], "active", "inactive"), ct);
                return;
            case "identity-account" when args.Length == 3:
                await operations.SetAccountEnabledAsync(Id(args[1]), State(args[2], "enabled", "disabled"), ct);
                return;
            case "identity-tenant" when args.Length == 4:
                var active = State(args[2], "active", "suspended");
                if (!DateTimeOffset.TryParseExact(args[3], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) throw Invalid();
                await operations.SetTenantAsync(Id(args[1]), active ? SubscriptionStatus.Active : SubscriptionStatus.Suspended, expiry, ct);
                return;
            default: throw Invalid();
        }
    }
    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw Invalid();
    private static bool State(string value, string yes, string no) => value == yes ? true : value == no ? false : throw Invalid();
    private static TenantContractException Invalid() => new("invalid_identity_command");
}
