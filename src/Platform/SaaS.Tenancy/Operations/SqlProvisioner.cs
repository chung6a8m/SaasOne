using Microsoft.Data.SqlClient;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;
namespace SaaS.Tenancy.Operations;

public sealed class SqlProvisioner(string connection)
{
    public async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        await using var sql = await SqlOperations.OpenAsync(connection, cancellationToken);
        var actor = await SqlOperations.ScalarAsync(sql, """
            SELECT CASE WHEN DB_NAME() = 'master'
                AND HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE') = 1
                AND HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY LOGIN') = 1
                AND HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER PERFORMANCE STATE') = 1
                THEN ORIGINAL_LOGIN() ELSE NULL END
            """, cancellationToken);
        return actor as string ?? throw new TenantContractException("provisioner_not_authorized");
    }

    public async Task<bool> CreateDatabaseAsync(string database, bool allowCreate, CancellationToken cancellationToken)
    {
        if (database != TenancyConnections.PlatformDatabase && (!database.StartsWith("saas_t_", StringComparison.Ordinal)
            || !Guid.TryParseExact(database[7..], "N", out _))) throw new TenantContractException("database_target_mismatch");
        await using var sql = await SqlOperations.OpenAsync(connection, cancellationToken);
        var exists = await SqlOperations.ScalarAsync(sql, "SELECT DB_ID(@database)", cancellationToken, ("@database", database));
        if (exists is not null and not DBNull) return false;
        if (!allowCreate) throw new TenantContractException("provisioned_database_missing");
        await SqlOperations.ExecuteAsync(sql, "DECLARE @ddl nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@database); EXEC sys.sp_executesql @ddl;", cancellationToken, ("@database", database));
        return true;
    }

    public async Task EnsurePrincipalAsync(string credential, bool migrator, CancellationToken cancellationToken)
    {
        var target = new SqlConnectionStringBuilder(credential);
        if (target.IntegratedSecurity || string.IsNullOrEmpty(target.UserID) || string.IsNullOrEmpty(target.Password))
            throw new TenantContractException("database_principal_mismatch");
        await using (var server = await SqlOperations.OpenAsync(connection, cancellationToken))
        {
            await SqlOperations.ExecuteAsync(server, """
                IF SUSER_ID(@login) IS NULL
                BEGIN
                    DECLARE @ddl nvarchar(max) = N'CREATE LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = '
                        + QUOTENAME(@secret, '''') + N', CHECK_POLICY = ON';
                    EXEC sys.sp_executesql @ddl;
                END
                """, cancellationToken, ("@login", target.UserID), ("@secret", target.Password));
        }
        var admin = new SqlConnectionStringBuilder(connection) { InitialCatalog = target.InitialCatalog };
        await using (var database = await SqlOperations.OpenAsync(admin.ConnectionString, cancellationToken))
        {
            await SqlOperations.ExecuteAsync(database, """
                IF USER_ID(@login) IS NULL
                BEGIN
                    DECLARE @ddl nvarchar(max) = N'CREATE USER ' + QUOTENAME(@login) + N' FOR LOGIN ' + QUOTENAME(@login);
                    EXEC sys.sp_executesql @ddl;
                END
                DECLARE @grant nvarchar(max) = N'GRANT CONNECT TO ' + QUOTENAME(@login);
                EXEC sys.sp_executesql @grant;
                IF @migrator = 1 AND IS_ROLEMEMBER('db_owner', @login) <> 1
                BEGIN
                    DECLARE @role nvarchar(max) = N'ALTER ROLE [db_owner] ADD MEMBER ' + QUOTENAME(@login);
                    EXEC sys.sp_executesql @role;
                END
                """, cancellationToken, ("@login", target.UserID), ("@migrator", migrator));
        }
        // An existing login is never reset or silently reassigned. Its supplied credential must authenticate.
        await using var verified = await SqlOperations.OpenAsync(credential, cancellationToken);
        if (await SqlOperations.ScalarAsync(verified, "SELECT ORIGINAL_LOGIN()", cancellationToken) as string != target.UserID)
            throw new TenantContractException("database_principal_mismatch");
    }

    public async Task GrantOperatorAsync(string credential, CancellationToken cancellationToken)
    {
        await EnsurePrincipalAsync(credential, false, cancellationToken);
        var target = new SqlConnectionStringBuilder(credential);
        var admin = new SqlConnectionStringBuilder(connection) { InitialCatalog = TenancyConnections.PlatformDatabase };
        await using var sql = await SqlOperations.OpenAsync(admin.ConnectionString, cancellationToken);
        await SqlOperations.ExecuteAsync(sql, """
            IF DATABASE_PRINCIPAL_ID('saas_operator') IS NULL CREATE ROLE [saas_operator];
            GRANT SELECT, INSERT, UPDATE ON SCHEMA::[platform] TO [saas_operator];
            GRANT SELECT, INSERT, UPDATE ON SCHEMA::[identity] TO [saas_operator];
            DENY INSERT, UPDATE, DELETE ON [platform].[RecoveryAuthorities] TO [saas_operator];
            DENY UPDATE, DELETE ON [platform].[OperationalAudit] TO [saas_operator];
            IF IS_ROLEMEMBER('saas_operator', @login) <> 1
            BEGIN
                DECLARE @ddl nvarchar(max) = N'ALTER ROLE [saas_operator] ADD MEMBER ' + QUOTENAME(@login);
                EXEC sys.sp_executesql @ddl;
            END
            """, cancellationToken, ("@login", target.UserID));
    }

    public async Task GrantRuntimeAsync(string credential, bool platform, CancellationToken cancellationToken)
    {
        await EnsurePrincipalAsync(credential, false, cancellationToken);
        var target = new SqlConnectionStringBuilder(credential);
        var admin = new SqlConnectionStringBuilder(connection) { InitialCatalog = target.InitialCatalog };
        await using var sql = await SqlOperations.OpenAsync(admin.ConnectionString, cancellationToken);
        await SqlOperations.ExecuteAsync(sql, """
            DECLARE @user nvarchar(258) = QUOTENAME(@login);
            DECLARE @schema nvarchar(258) = CASE WHEN @platform = 1 THEN N'[platform]' ELSE N'[tenancy]' END;
            DECLARE @ddl nvarchar(max) = N'GRANT SELECT ON SCHEMA::' + @schema + N' TO ' + @user + N';'
                + N'DENY INSERT, UPDATE, DELETE ON SCHEMA::' + @schema + N' TO ' + @user + N';';
            SELECT @ddl = @ddl + N'GRANT SELECT, INSERT, UPDATE, DELETE ON [dbo].' + QUOTENAME([name]) + N' TO ' + @user + N';'
            FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo') AND [name] LIKE N'oc[_]%';
            EXEC sys.sp_executesql @ddl;
            """, cancellationToken, ("@login", target.UserID), ("@platform", platform));
        await SqlOperations.RequireRuntimeAsync(credential, cancellationToken);
    }

    public async Task AssertPriorOwnerStoppedAsync(Guid operationId, Guid? ownerId, string? priorHost, string host, CancellationToken cancellationToken)
    {
        if (ownerId is null) return;
        if (priorHost != host) throw new TenantContractException("cross_host_reconciliation_required");
        await AuthenticateAsync(cancellationToken); // Prove visibility of all server sessions, not only our own.
        await using var sql = await SqlOperations.OpenAsync(connection, cancellationToken);
        var count = await SqlOperations.ScalarAsync(sql, "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = @tag", cancellationToken,
            ("@tag", TenancyConnections.OperationTag(operationId, ownerId.Value)));
        if (count is not int value || value != 0) throw new TenantContractException("prior_owner_still_active");
    }
}
