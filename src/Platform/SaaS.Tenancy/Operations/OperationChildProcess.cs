using System.Diagnostics;
using System.Text.Json;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
namespace SaaS.Tenancy.Operations;

/// <summary>
/// Durable launch intent closes the crash-before-SQL-connect gap of external EF bundles.
/// An ambiguous launch is deliberately not reclaimed by timeout. The operator must prove
/// process and server quiescence before manually reconciling that exceptional state.
/// </summary>
public static class OperationChildProcess
{
    private sealed record Marker(int Version, string Resource, Guid OperationId, Guid OwnerId, int? ProcessId, long? StartUtcTicks);

    public static async Task RunAsync(string executable, string workingDirectory, string connection,
        string resource, Guid operationId, Guid ownerId, CancellationToken ct)
    {
        var path = PathFor(resource);
        if (File.Exists(path)) throw new TenantContractException("operation_child_reconciliation_required");
        var marker = new Marker(1, resource, operationId, ownerId, null, null);
        await WriteAsync(path, marker, ct); // Intent is durable BEFORE Process.Start.
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("SAAS_", StringComparison.OrdinalIgnoreCase)
            || x.StartsWith("MSSQL_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment.Remove("DOTNET_STARTUP_HOOKS");
        start.Environment.Remove("DOTNET_ADDITIONAL_DEPS");
        start.Environment["SAAS_BUNDLE_EXECUTION"] = "1";
        start.Environment["SAAS_MIGRATION_CONNECTION"] = connection;
        Process? child = null;
        Task drainOut = Task.CompletedTask, drainError = Task.CompletedTask;
        try
        {
            child = Process.Start(start) ?? throw new TenantContractException("bundle_launch_failed");
            drainOut = child.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            drainError = child.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            marker = marker with { ProcessId = child.Id, StartUtcTicks = child.StartTime.ToUniversalTime().Ticks };
            await WriteAsync(path, marker, CancellationToken.None);
            await child.WaitForExitAsync(ct);
            await Task.WhenAll(drainOut, drainError);
            await DemandNoSqlSessionsAsync(marker, CancellationToken.None);
            File.Delete(path);
            if (child.ExitCode != 0) throw new TenantContractException("migration_bundle_failed");
        }
        catch
        {
            if (child is not null)
            {
                // Never detach an unobserved mutating child while releasing the resource lease.
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(drainOut, drainError);
                try { await DemandNoSqlSessionsAsync(marker, CancellationToken.None); File.Delete(path); }
                catch { /* Retain the marker until both process and SQL quiescence can be proved. */ }
            }
            throw;
        }
        finally { child?.Dispose(); }
    }

    internal static async Task DemandQuiescentAsync(string resource, CancellationToken ct)
    {
        var path = PathFor(resource);
        if (!File.Exists(path)) return;
        Marker marker;
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > 4096) throw new JsonException();
            marker = await JsonSerializer.DeserializeAsync<Marker>(input, cancellationToken: ct) ?? throw new JsonException();
        }
        catch (JsonException) { throw new TenantContractException("operation_child_reconciliation_required"); }
        if (marker.Version != 1 || marker.Resource != resource || marker.OperationId == Guid.Empty || marker.OwnerId == Guid.Empty
            || marker.ProcessId is null or <= 0 || marker.StartUtcTicks is null or <= 0)
            throw new TenantContractException("operation_child_reconciliation_required");
        try
        {
            using var process = Process.GetProcessById(marker.ProcessId.Value);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == marker.StartUtcTicks)
                throw new TenantContractException("prior_owner_still_active");
        }
        catch (ArgumentException) { /* PID absent. PID reuse is checked against the recorded start time. */ }
        await DemandNoSqlSessionsAsync(marker, ct);
        File.Delete(path);
    }

    private static async Task DemandNoSqlSessionsAsync(Marker marker, CancellationToken ct)
    {
        var connection = new TenancyConnections().Provisioner();
        await new SqlProvisioner(connection).AuthenticateAsync(ct);
        await using var sql = await SqlOperations.OpenAsync(connection, ct);
        var count = await SqlOperations.ScalarAsync(sql, "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = @tag", ct,
            ("@tag", TenancyConnections.OperationTag(marker.OperationId, marker.OwnerId)));
        if (count is not int value || value != 0) throw new TenantContractException("prior_owner_still_active");
    }
    private static string PathFor(string resource)
    {
        if (resource != PlatformReleaseBarrier.Resource && (!resource.StartsWith("tenant-", StringComparison.Ordinal) || !Guid.TryParseExact(resource[7..], "N", out var id) || id == Guid.Empty))
            throw new TenantContractException("invalid_child_resource");
        return Path.Combine(Path.GetFullPath(TenancyConnections.Required("SAAS_OPERATIONS_HOME")), resource + ".child.json");
    }
    private static async Task WriteAsync(string path, Marker marker, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, marker, cancellationToken: ct);
                await file.FlushAsync(ct); file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
