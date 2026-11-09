using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrchardCore.Data.Migration.Records;
using OrchardCore.Environment.Shell.Descriptor.Models;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Operations;
namespace SaaS.Web.Orchard;

public static class FoundationProfile
{
    // No Users/Administrator, content scripting, templates, recipes UI, feature UI or upload surface is enabled.
    public static readonly string[] Features = new[] { "SaaS.Web", OrchardCore.Modules.Application.DefaultFeatureId, "SaaS.Theme.App" }
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static string FeatureHash => Hash(string.Join('\n', Features));
    public static string MigrationHash => Hash(typeof(FoundationMigration).FullName + ":1");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static void VerifyFeatures(IEnumerable<string> features)
    {
        var actual = features.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(Features, StringComparer.Ordinal)) throw new TenantContractException("orchard_feature_profile_mismatch");
    }

    public static async Task<OrchardBaseline> ReadSnapshotAsync(string connection, CancellationToken cancellationToken)
    {
        await using var sql = await SqlOperations.OpenAsync(connection, cancellationToken);
        return await ReadSnapshotAsync(sql, cancellationToken);
    }

    public static async Task<OrchardBaseline> ReadSnapshotAsync(SqlConnection sql, CancellationToken cancellationToken)
    {
        var marker = await SqlOperations.ScalarAsync(sql,
            "SELECT COUNT(*) FROM [dbo].[oc_SaaSFoundation] WHERE [Id] = 1 AND [Version] = 1", cancellationToken);
        if (marker is not int count || count != 1) throw new TenantContractException("orchard_foundation_mismatch");
        await using var command = SqlOperations.Command(sql, """
            SELECT [Type], [Content] FROM [dbo].[oc_Document]
            WHERE [Type] LIKE N'%ShellDescriptor%' OR [Type] LIKE N'%DataMigrationRecord%'
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var descriptors = 0;
        var migrations = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetString(0);
            using var document = JsonDocument.Parse(reader.GetString(1));
            if (IsType(type, typeof(ShellDescriptor)))
            {
                descriptors++;
                var descriptor = document.RootElement;
                if (Property(descriptor, "SerialNumber").GetInt32() < 1) throw new TenantContractException("orchard_descriptor_missing");
                VerifyFeatures(Property(descriptor, "Features").EnumerateArray().Select(item => Property(item, "Id").GetString() ?? string.Empty));
            }
            else if (IsType(type, typeof(DataMigrationRecord)))
            {
                migrations++;
                var items = Property(document.RootElement, "DataMigrations").EnumerateArray().ToArray();
                if (items.Length != 1 || Property(items[0], "DataMigrationClass").GetString() != typeof(FoundationMigration).FullName
                    || Property(items[0], "Version").GetInt32() != 1)
                    throw new TenantContractException("orchard_migration_mismatch");
            }
            else throw new TenantContractException("orchard_document_type_mismatch");
        }
        if (descriptors != 1 || migrations != 1) throw new TenantContractException("orchard_baseline_missing");
        return new(FeatureHash, MigrationHash);
    }
    private static bool IsType(string stored, Type type) => stored == type.FullName || stored.StartsWith(type.FullName + ",", StringComparison.Ordinal);
    private static JsonElement Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new TenantContractException("orchard_document_invalid");
    }
}
