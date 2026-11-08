namespace SaaS.Abstractions.Tenancy;

/// <summary>Factories stay offline for tooling. The controlled runner injects credentials
/// only into the EF bundle child's environment, never command arguments or package files.</summary>
public static class BundleConnection
{
    public static string ForDesignTime(string offlineConnection)
    {
        var execution = Environment.GetEnvironmentVariable("SAAS_BUNDLE_EXECUTION");
        if (execution is null) return offlineConnection;
        if (execution != "1" || Environment.GetEnvironmentVariable("SAAS_MIGRATION_CONNECTION") is not { Length: > 0 } connection)
            throw new TenantContractException("bundle_connection_required");
        return connection;
    }
}
