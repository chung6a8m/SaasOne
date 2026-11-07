using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
using SaaS.Web.Orchard;
namespace SaaS.Web.Maintenance;

public static class MaintenanceCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            var service = new TenancyProvisioning(new TenancyConnections(), new OrchardMaintenance());
            if (args.Length > 0 && args[0].StartsWith("identity-", StringComparison.Ordinal))
                await IdentityMaintenanceCommand.ExecuteAsync(args, cancellationToken);
            else if (args.Length == 2 && args[0] == "bootstrap" && Guid.TryParseExact(args[1], "D", out var installationId))
                await service.BootstrapAsync(installationId, cancellationToken);
            else if (args.Length > 0 && args[0] == "provision")
                await service.ProvisionAsync(ProvisionRequest.Parse(args), cancellationToken);
            else throw new TenantContractException("invalid_maintenance_command");
            Console.WriteLine("maintenance_completed");
            return 0;
        }
        catch (TenantContractException error) { Console.Error.WriteLine(error.Code); return 1; }
        catch (OperationCanceledException) { Console.Error.WriteLine("maintenance_cancelled"); return 1; }
        catch (Exception error)
        {
            // No provider messages, SQL text, argument values or connection material are emitted.
            Console.Error.WriteLine("maintenance_failed:" + error.GetType().Name);
            if (error is SqlException sql) Console.Error.WriteLine("sql_error_number:" + sql.Number);
            foreach (var frame in new StackTrace(error, false).GetFrames().Take(12))
            {
                var method = frame.GetMethod();
                if (method?.DeclaringType?.Namespace?.StartsWith("SaaS.", StringComparison.Ordinal) == true)
                    Console.Error.WriteLine("source:" + method.DeclaringType.FullName + "." + method.Name);
            }
            return 1;
        }
    }
}
