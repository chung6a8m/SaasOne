using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
using SaaS.Tenancy.Operations;
namespace SaaS.Tenancy.Release;

/// <summary>
/// Private shared store: the CLI and SQL Server see the SAME protected files through
/// different roots. V1 supports one COPY_ONLY full backup, never relocation or partial chains.
/// The signing key, manifest and platform audit are outside the restored customer database.
/// </summary>
public sealed class SqlBackupStore : IDisposable
{
    private readonly string _root;
    private readonly string _sqlRoot;
    private readonly byte[] _key;
    private const int TimeoutSeconds = 1800;

    public SqlBackupStore()
    {
        if (TenancyConnections.Required("SAAS_BACKUP_STORE_ACK") != "private-shared-store-v1")
            throw new TenantContractException("backup_store_acknowledgement_required");
        _root = Path.GetFullPath(TenancyConnections.Required("SAAS_BACKUP_ROOT"));
        _sqlRoot = TenancyConnections.Required("SAAS_SQL_BACKUP_ROOT").TrimEnd('/', '\\');
        if (!Directory.Exists(_root) || _sqlRoot.Length is < 1 or > 1024 || _sqlRoot.Any(char.IsControl)
            || !(_sqlRoot.StartsWith('/') || _sqlRoot.Length > 2 && char.IsAsciiLetter(_sqlRoot[0]) && _sqlRoot[1] == ':' && _sqlRoot[2] == '\\'))
            throw new TenantContractException("backup_store_invalid");
        _ = ArtifactPath.Resolve(_root, "probe");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(_root)
            & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new TenantContractException("backup_store_permissions_invalid");
        try { _key = Convert.FromBase64String(TenancyConnections.Required("SAAS_BACKUP_MANIFEST_KEY")); }
        catch (FormatException) { throw new TenantContractException("backup_signing_key_invalid"); }
        if (_key.Length != 32) throw new TenantContractException("backup_signing_key_invalid");
    }

    public async Task<VerifiedBackup> OpenAsync(Guid tenantId, Guid backupId, CancellationToken ct)
    {
        DemandIds(tenantId, backupId);
        FileStream? manifest = null, data = null;
        try
        {
            manifest = Read(backupId, "manifest.json");
            if (manifest.Length is < 1 or > 64 * 1024) throw new TenantContractException("backup_manifest_invalid");
            using var buffer = new MemoryStream(); await manifest.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray(); var value = BackupManifestCodec.Verify(bytes, _key, tenantId);
            if (value.BackupId != backupId) throw new TenantContractException("backup_manifest_mismatch");
            data = Read(backupId, "database.bak");
            var result = new VerifiedBackup(value, Convert.ToHexString(SHA256.HashData(bytes)), SqlPath(backupId), manifest, data);
            await result.VerifyBytesAsync(ct);
            return result;
        }
        catch
        {
            if (data is not null) await data.DisposeAsync();
            if (manifest is not null) await manifest.DisposeAsync();
            throw;
        }
    }

    internal async Task<VerifiedBackup> CreateAsync(Guid tenantId, Guid backupId, string serverConnection, bool resume, CancellationToken ct)
    {
        DemandIds(tenantId, backupId);
        var directory = ArtifactPath.Resolve(_root, backupId.ToString("N"));
        if (Directory.Exists(directory) && !resume) throw new TenantContractException("backup_already_exists");
        if (File.Exists(PathFor(backupId, "manifest.json")))
        {
            var existing = await OpenAsync(tenantId, backupId, ct);
            try { await VerifySqlAsync(existing, serverConnection, ct); return existing; }
            catch { await existing.DisposeAsync(); throw; }
        }
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        var dataPath = PathFor(backupId, "database.bak");
        // Only an unsealed interrupted backup may be recreated, after the coordinator proves
        // prior-owner quiescence. A sealed source is immutable and never overwritten on retry.
        await using (var placeholder = new FileStream(dataPath, resume ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { await placeholder.FlushAsync(ct); placeholder.Flush(flushToDisk: true); }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dataPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        await using var sql = await SqlOperations.OpenAsync(serverConnection, ct);
        await using (var command = Long(sql, """
            DECLARE @ddl nvarchar(max) = N'BACKUP DATABASE ' + QUOTENAME(@database)
                + N' TO DISK = @file WITH COPY_ONLY, INIT, CHECKSUM';
            EXEC sys.sp_executesql @ddl, N'@file nvarchar(4000)', @file = @file;
            """, ("@database", TenancyConnections.TenantDatabase(tenantId)), ("@file", SqlPath(backupId))))
            await command.ExecuteNonQueryAsync(ct);
        var header = await ReadHeaderAsync(sql, SqlPath(backupId), ct);
        DemandHeader(header, TenancyConnections.TenantDatabase(tenantId), null);
        long length; string digest;
        await using (var data = Read(backupId, "database.bak"))
        { length = data.Length; digest = Convert.ToHexString(await SHA256.HashDataAsync(data, ct)); }
        var value = new BackupManifest(1, tenantId, backupId, TenancyConnections.TenantDatabase(tenantId), DateTimeOffset.UtcNow,
            [new("database.bak", length, digest, header.BackupSetId, 1)]);
        var sealedBytes = BackupManifestCodec.Sign(value, _key);
        var temporary = PathFor(backupId, "manifest-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(sealedBytes, ct); await output.FlushAsync(ct); output.Flush(flushToDisk: true); }
            File.Move(temporary, PathFor(backupId, "manifest.json"), overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var created = await OpenAsync(tenantId, backupId, ct);
        try { await VerifySqlAsync(created, serverConnection, ct); return created; }
        catch { await created.DisposeAsync(); throw; }
    }

    public async Task VerifySqlAsync(VerifiedBackup backup, string serverConnection, CancellationToken ct)
    {
        if (backup.SqlFile != SqlPath(backup.Manifest.BackupId)) throw new TenantContractException("backup_store_mismatch");
        await backup.VerifyBytesAsync(ct);
        await using var sql = await SqlOperations.OpenAsync(serverConnection, ct);
        var header = await ReadHeaderAsync(sql, backup.SqlFile, ct);
        DemandHeader(header, backup.Manifest.Database, backup.Manifest.Parts[0].BackupSetId);
        await DemandSameFilesAsync(sql, backup, ct);
        await using var verify = Long(sql, "RESTORE VERIFYONLY FROM DISK = @file WITH FILE = 1, CHECKSUM", ("@file", backup.SqlFile));
        await verify.ExecuteNonQueryAsync(ct);
    }

    internal async Task RestoreAsync(VerifiedBackup backup, string serverConnection, CancellationToken ct)
    {
        // Revalidate immediately before overwrite; both read handles stay alive through SQL use.
        await VerifySqlAsync(backup, serverConnection, ct);
        await using var sql = await SqlOperations.OpenAsync(serverConnection, ct);
        await using var restore = Long(sql, """
            DECLARE @ddl nvarchar(max) = CASE WHEN EXISTS (SELECT 1 FROM sys.databases WHERE name = @database AND state = 1)
                THEN N'' ELSE N'ALTER DATABASE ' + QUOTENAME(@database) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;' END
                + N'RESTORE DATABASE ' + QUOTENAME(@database) + N' FROM DISK = @file WITH FILE = 1, REPLACE, RECOVERY, CHECKSUM;'
                + N'ALTER DATABASE ' + QUOTENAME(@database) + N' SET MULTI_USER;';
            EXEC sys.sp_executesql @ddl, N'@file nvarchar(4000)', @file = @file;
            """, ("@database", backup.Manifest.Database), ("@file", backup.SqlFile));
        // No finally block opens either the SQL database or the application gate on failure.
        await restore.ExecuteNonQueryAsync(ct);
    }

    private sealed record Header(string Database, Guid BackupSetId, int Position, int Type, bool Checksums, bool CopyOnly);
    private static async Task<Header> ReadHeaderAsync(SqlConnection sql, string path, CancellationToken ct)
    {
        await using var command = Long(sql, "RESTORE HEADERONLY FROM DISK = @file", ("@file", path));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new TenantContractException("backup_header_invalid");
        var header = new Header(Convert.ToString(reader["DatabaseName"])!, (Guid)reader["BackupSetGUID"],
            Convert.ToInt32(reader["Position"]), Convert.ToInt32(reader["BackupType"]),
            Convert.ToBoolean(reader["HasBackupChecksums"]), Convert.ToBoolean(reader["IsCopyOnly"]));
        if (await reader.ReadAsync(ct)) throw new TenantContractException("backup_chain_unsupported");
        return header;
    }
    private static void DemandHeader(Header header, string database, Guid? backupSet)
    {
        if (header.Database != database || header.BackupSetId == Guid.Empty || backupSet is not null && header.BackupSetId != backupSet
            || header.Position != 1 || header.Type != 1 || !header.Checksums || !header.CopyOnly)
            throw new TenantContractException("backup_header_mismatch");
    }
    private static async Task DemandSameFilesAsync(SqlConnection sql, VerifiedBackup backup, CancellationToken ct)
    {
        var source = new Dictionary<string, (string Path, string Type)>(StringComparer.Ordinal);
        await using (var command = Long(sql, "RESTORE FILELISTONLY FROM DISK = @file WITH FILE = 1", ("@file", backup.SqlFile)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var type = Convert.ToString(reader["Type"])!;
                if (type is not ("D" or "L") || !Convert.ToBoolean(reader["IsPresent"])
                    || !source.TryAdd(Convert.ToString(reader["LogicalName"])!, (Convert.ToString(reader["PhysicalName"])!, type)))
                    throw new TenantContractException("backup_file_layout_unsupported");
            }
        var destination = new Dictionary<string, (string Path, string Type)>(StringComparer.Ordinal);
        await using (var command = Long(sql, "SELECT name, physical_name, type FROM sys.master_files WHERE database_id = DB_ID(@database)",
            ("@database", backup.Manifest.Database)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetByte(2) switch { 0 => "D", 1 => "L", _ => "unsupported" };
                destination.Add(reader.GetString(0), (reader.GetString(1), type));
            }
        if (source.Count is < 2 or > 32 || source.Count != destination.Count
            || source.Values.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != source.Count
            || source.Any(x => !destination.TryGetValue(x.Key, out var target) || target != x.Value))
            throw new TenantContractException("backup_file_layout_unsupported");
        // No MOVE / cloning / arbitrary target paths. Even a valid signed backup cannot select
        // a physical file belonging to a different current database.
        foreach (var file in destination.Values)
            if (Convert.ToInt32(await SqlOperations.ScalarAsync(sql,
                "SELECT COUNT(*) FROM sys.master_files WHERE physical_name = @file AND database_id <> DB_ID(@database)", ct,
                ("@file", file.Path), ("@database", backup.Manifest.Database))) != 0)
                throw new TenantContractException("backup_file_target_conflict");
    }
    private static SqlCommand Long(SqlConnection sql, string text, params (string Name, object? Value)[] parameters)
    { var command = SqlOperations.Command(sql, text, parameters); command.CommandTimeout = TimeoutSeconds; return command; }
    private string PathFor(Guid id, string file) => ArtifactPath.Resolve(_root, id.ToString("N") + "/" + file);
    private FileStream Read(Guid id, string file) => new(PathFor(id, file), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
    private string SqlPath(Guid id) => _sqlRoot + (_sqlRoot.StartsWith('/') ? "/" : "\\") + id.ToString("N")
        + (_sqlRoot.StartsWith('/') ? "/" : "\\") + "database.bak";
    private static void DemandIds(Guid tenant, Guid backup)
    { if (tenant == Guid.Empty || backup == Guid.Empty) throw new TenantContractException("invalid_backup_request"); }
    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}

public sealed class VerifiedBackup : IAsyncDisposable
{
    private readonly FileStream _manifest;
    private readonly FileStream _data;
    public BackupManifest Manifest { get; }
    public string ManifestSha256 { get; }
    internal string SqlFile { get; }
    internal VerifiedBackup(BackupManifest manifest, string hash, string sqlFile, FileStream metadata, FileStream data)
        => (Manifest, ManifestSha256, SqlFile, _manifest, _data) = (manifest, hash, sqlFile, metadata, data);
    public async Task VerifyBytesAsync(CancellationToken ct)
    {
        _data.Position = 0;
        var part = Manifest.Parts[0];
        if (_data.Length != part.Length || !Digest.EqualsSha256(Convert.ToHexString(await SHA256.HashDataAsync(_data, ct)), part.Sha256))
            throw new TenantContractException("backup_artifact_changed");
    }
    public async ValueTask DisposeAsync() { await _data.DisposeAsync(); await _manifest.DisposeAsync(); }
}
