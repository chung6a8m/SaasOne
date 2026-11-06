namespace SaaS.Web.Maintenance;

public static class MaintenanceCommand
{
    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default) =>
        Task.FromException<int>(new NotImplementedException("Protected M1 provisioning has not been implemented."));
}
