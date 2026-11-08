using Microsoft.Data.SqlClient;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
namespace SaaS.Tenancy.Operations;

/// <summary>
/// A same-host process fence plus a server-side session lock. Neither expires with elapsed time.
/// A crashed owner's SQL sessions and external migration process must stop before takeover.
/// </summary>
public sealed class OperationLease : IAsyncDisposable
{
    private readonly FileStream _file;
    public SqlConnection Connection { get; }
    public string HostId { get; }
    public string Resource { get; }
    private OperationLease(FileStream file, SqlConnection connection, string hostId, string resource) => (_file, Connection, HostId, Resource) = (file, connection, hostId, resource);

    public static Task<OperationLease> AcquireAsync(string connection, string resource, CancellationToken cancellationToken)
        => AcquireCoreAsync(connection, resource, false, cancellationToken);

    // Only the physical restore workflow may cross its own durable pending-source barrier.
    // Generic recovery, plan, release, provisioning and purge paths cannot skip that checkpoint.
    internal static Task<OperationLease> AcquireRestoreAsync(string connection, string resource, CancellationToken cancellationToken)
        => AcquireCoreAsync(connection, resource, true, cancellationToken);

    private static async Task<OperationLease> AcquireCoreAsync(string connection, string resource, bool physicalRestore, CancellationToken cancellationToken)
    {
        var home = Path.GetFullPath(TenancyConnections.Required("SAAS_OPERATIONS_HOME"));
        Directory.CreateDirectory(home);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FileStream file;
        try { file = new FileStream(Path.Combine(home, resource + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new TenantContractException("operation_busy"); }
        SqlConnection? sql = null;
        try
        {
            if (resource == PlatformReleaseBarrier.Resource || resource.StartsWith("tenant-", StringComparison.Ordinal))
                await OperationChildProcess.DemandQuiescentAsync(resource, cancellationToken);
            var hostFile = Path.Combine(home, "host-id");
            if (!File.Exists(hostFile))
            {
                try
                {
                    await using var created = new FileStream(hostFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await using var writer = new StreamWriter(created);
                    await writer.WriteAsync(Guid.NewGuid().ToString("N").AsMemory(), cancellationToken);
                    await writer.FlushAsync(cancellationToken);
                    created.Flush(flushToDisk: true);
                }
                catch (IOException) { throw new TenantContractException("operation_host_initializing"); }
            }
            var hostId = (await File.ReadAllTextAsync(hostFile, cancellationToken)).Trim();
            if (!Guid.TryParseExact(hostId, "N", out _)) throw new TenantContractException("invalid_operation_host");
            sql = await SqlOperations.OpenAsync(connection, cancellationToken);
            if (resource != "platform-bootstrap")
            {
                var global = await SqlOperations.ScalarAsync(sql, """
                    DECLARE @result int;
                    EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = @mode, @LockOwner = 'Session', @LockTimeout = 0;
                    SELECT @result;
                    """, cancellationToken, ("@resource", PlatformReleaseBarrier.Lock),
                    ("@mode", resource == PlatformReleaseBarrier.Resource ? "Exclusive" : "Shared"));
                if (global is not int granted || granted < 0) throw new TenantContractException("platform_release_busy");
                if (resource != PlatformReleaseBarrier.Resource) await PlatformReleaseBarrier.DemandClearAsync(sql, cancellationToken);
            }
            var status = await SqlOperations.ScalarAsync(sql, """
                DECLARE @status int;
                EXEC @status = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
                SELECT @status;
                """, cancellationToken, ("@resource", resource));
            if (status is not int result || result < 0) throw new TenantContractException("operation_busy");
            if (!physicalRestore) await PhysicalRestoreBarrier.DemandClearAsync(sql, resource, cancellationToken);
            return new(file, sql, hostId, resource);
        }
        catch
        {
            if (sql is not null) await sql.DisposeAsync();
            await file.DisposeAsync();
            throw;
        }
    }

    public async Task AssertHeldAsync(CancellationToken cancellationToken)
    {
        var mode = await SqlOperations.ScalarAsync(Connection, "SELECT APPLOCK_MODE('public', @resource, 'Session')", cancellationToken, ("@resource", Resource));
        if (mode as string != "Exclusive") throw new TenantContractException("operation_lock_lost");
        if (Resource != "platform-bootstrap")
        {
            var global = await SqlOperations.ScalarAsync(Connection, "SELECT APPLOCK_MODE('public', @resource, 'Session')", cancellationToken,
                ("@resource", PlatformReleaseBarrier.Lock));
            if (global as string != (Resource == PlatformReleaseBarrier.Resource ? "Exclusive" : "Shared"))
                throw new TenantContractException("operation_lock_lost");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Do not unlink lock files: deleting an open lock file could let another process lock a new inode.
        await Connection.DisposeAsync();
        await _file.DisposeAsync();
    }
}
