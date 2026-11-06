using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaS.Abstractions.Tenancy;
namespace SaaS.Tenancy.Data;

/// <summary>Secret references come from deployment configuration, never from HTTP input.</summary>
public sealed class TenancyConnections
{
    public const string PlatformDatabase = "saas_platform";
    public static string TenantSecret(Guid id, string role) => $"SAAS_TENANT_{id:N}_{role}".ToUpperInvariant();
    public static string TenantDatabase(Guid id) => "saas_t_" + id.ToString("N");
    public static bool RuntimeConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SAAS_PLATFORM_RUNTIME_CONNECTION"));
    public static string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
        ? value : throw new TenantContractException("missing_deployment_secret");

    public string Platform(string role) => Validate(Required($"SAAS_PLATFORM_{role.ToUpperInvariant()}_CONNECTION"), PlatformDatabase, "saas_platform_" + role.ToLowerInvariant());
    public string Customer(Guid id, string role) => Validate(Required(TenantSecret(id, role)), TenantDatabase(id), (role.Equals("runtime", StringComparison.OrdinalIgnoreCase) ? "saas_rt_" : "saas_mg_") + id.ToString("N"));
    public string Provisioner() => Validate(Required("SAAS_PROVISIONER_CONNECTION"), "master", null);

    public string Validate(string connection, string database, string? login)
    {
        SqlConnectionStringBuilder value;
        try { value = new(connection); }
        catch (ArgumentException) { throw new TenantContractException("invalid_database_connection"); }
        if (!string.Equals(value.DataSource, Required("SAAS_SQL_SERVER"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(value.InitialCatalog, database, StringComparison.Ordinal) || !string.IsNullOrEmpty(value.AttachDBFilename)
            || value.UserInstance || value.ConnectTimeout is < 1 or > 60)
            throw new TenantContractException("database_target_mismatch");
        if (login is not null && (value.IntegratedSecurity || value.UserID != login || string.IsNullOrEmpty(value.Password)))
            throw new TenantContractException("database_principal_mismatch");
        value.Enlist = false;
        value.Pooling = false;
        value.PersistSecurityInfo = false;
        value.ApplicationName = "SaaS.Runtime";
        return value.ConnectionString;
    }

    public static string ForOperation(string connection, Guid operationId, Guid ownerId)
    {
        var value = new SqlConnectionStringBuilder(connection) { ApplicationName = OperationTag(operationId, ownerId), Pooling = false, Enlist = false };
        return value.ConnectionString;
    }
    public static string OperationTag(Guid operationId, Guid ownerId) => $"SaaS.Op.{operationId:N}.{ownerId:N}";
    public static PlatformDbContext PlatformContext(string connection) => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "platform")).Options);
    public static PlatformDbContext PlatformContext(SqlConnection connection) => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "platform")).Options);
    public static TenantDbContext CustomerContext(string connection) => new(new DbContextOptionsBuilder<TenantDbContext>()
        .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "tenancy")).Options);

    public static async Task VerifySchemaAsync(DbContext context, CancellationToken cancellationToken)
    {
        var expected = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).Order(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0 || !expected.SequenceEqual(applied, StringComparer.Ordinal))
            throw new TenantContractException("database_schema_mismatch");
    }
}
