using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

const string expectedDatabase = "saas_m0_smoke";
const string expectedVersion = "16.0.4305.5";
var secret = Environment.GetEnvironmentVariable("SAAS_SQL_SMOKE_CONNECTION");
if (string.IsNullOrWhiteSpace(secret))
{
    Console.Error.WriteLine("BLOCKED: SAAS_SQL_SMOKE_CONNECTION is required.");
    return 2;
}
try
{
    var connection = new SqlConnectionStringBuilder(secret);
    if (connection.InitialCatalog != expectedDatabase) throw new InvalidOperationException("Unexpected SQL target; only the dedicated smoke database is allowed.");
    if (connection.Encrypt == SqlConnectionEncryptOption.Optional) throw new InvalidOperationException("SQL encryption is required.");
    if (connection.TrustServerCertificate && !connection.DataSource.StartsWith("127.0.0.1,", StringComparison.Ordinal))
        throw new InvalidOperationException("TrustServerCertificate is allowed only for the disposable loopback fixture.");
    connection.ConnectTimeout = 15;
    var options = new DbContextOptionsBuilder().UseSqlServer(connection.ConnectionString).Options;
    await using var context = new DbContext(options);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await context.Database.OpenConnectionAsync(timeout.Token);
    await using var command = context.Database.GetDbConnection().CreateCommand();
    command.CommandTimeout = 15;
    command.CommandText = """
        SELECT DB_NAME(), CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
               CONVERT(int, SERVERPROPERTY('EngineEdition')),
               CONVERT(nvarchar(128), SERVERPROPERTY('Edition')), @@VERSION,
               IS_SRVROLEMEMBER('sysadmin'), HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'), 1;
        """;
    await using var reader = await command.ExecuteReaderAsync(timeout.Token);
    if (!await reader.ReadAsync(timeout.Token)) throw new InvalidOperationException("SQL probe returned no row.");
    var database = reader.GetString(0);
    var version = reader.GetString(1);
    var engine = reader.GetInt32(2);
    var edition = reader.GetString(3);
    var platform = reader.GetString(4);
    if (database != expectedDatabase || version != expectedVersion || engine != 3 || !edition.Contains("Developer", StringComparison.Ordinal)
        || !platform.Contains("Linux", StringComparison.Ordinal) || reader.GetInt32(5) != 0 || reader.GetInt32(6) != 0 || reader.GetInt32(7) != 1)
        throw new InvalidOperationException("SQL engine/version/database/platform/least-privilege probe did not match the pinned baseline.");
    var report = new
    {
        status = "PASS", sourceSha = Environment.GetEnvironmentVariable("SAAS_SOURCE_SHA"),
        timestampUtc = DateTimeOffset.UtcNow, provider = context.Database.ProviderName,
        database, productVersion = version, engineEdition = engine, edition, platform = "Linux",
        sysadmin = false, canCreateTable = false, queryResult = 1,
        discovered = 1, executed = 1, passed = 1, failed = 0, skipped = 0
    };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    var output = Path.Combine("artifacts", "verification", "sql-smoke.json");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, json + Environment.NewLine, timeout.Token);
    Console.WriteLine(json);
    return 0;
}
catch (SqlException error)
{
    Console.Error.WriteLine($"FAIL: SQL connection/query error {error.Number}; credentials and server details withheld.");
    return 1;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or OperationCanceledException or IOException)
{
    // Do not print exception messages: connection-string parser errors may contain input.
    Console.Error.WriteLine($"FAIL: SQL baseline/configuration probe ({error.GetType().Name}); no secret values logged.");
    return 1;
}
