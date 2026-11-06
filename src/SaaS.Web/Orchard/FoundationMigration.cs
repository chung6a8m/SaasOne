using OrchardCore.Data.Migration;
using OrchardCore.Environment.Shell;
using SaaS.Tenancy.Operations;
namespace SaaS.Web.Orchard;

/// <summary>Owned and versioned by Orchard, not by either EF migration stream.</summary>
public sealed class FoundationMigration(ShellSettings settings) : DataMigration
{
    public async Task<int> CreateAsync()
    {
        await using var sql = await SqlOperations.OpenAsync(settings["ConnectionString"], CancellationToken.None);
        // SQL Server is the accepted M0 provider. This step is safe to repeat after an interrupted DDL/record commit.
        await SqlOperations.ExecuteAsync(sql, """
            IF OBJECT_ID(N'[dbo].[oc_SaaSFoundation]', N'U') IS NULL
                CREATE TABLE [dbo].[oc_SaaSFoundation] (
                    [Id] int NOT NULL CONSTRAINT [PK_oc_SaaSFoundation] PRIMARY KEY,
                    [Version] int NOT NULL,
                    CONSTRAINT [CK_oc_SaaSFoundation] CHECK ([Id] = 1 AND [Version] = 1));
            IF NOT EXISTS (SELECT 1 FROM [dbo].[oc_SaaSFoundation])
                INSERT INTO [dbo].[oc_SaaSFoundation] ([Id], [Version]) VALUES (1, 1);
            IF (SELECT COUNT(*) FROM [dbo].[oc_SaaSFoundation] WHERE [Id] = 1 AND [Version] = 1) <> 1
                THROW 51000, 'orchard_foundation_mismatch', 1;
            """, CancellationToken.None);
        return 1;
    }
}
