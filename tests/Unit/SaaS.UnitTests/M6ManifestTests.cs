using System.Security.Cryptography;
using System.Text;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Release;
using Xunit;

namespace SaaS.UnitTests;

public sealed class M6ManifestTests
{
    [Fact]
    public void Backup_manifest_authenticates_origin_and_detects_mutation()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var manifest = Backup();
        var bytes = BackupManifestCodec.Sign(manifest, key);
        Assert.Equal(manifest.TenantId, BackupManifestCodec.Verify(bytes, key, manifest.TenantId).TenantId);
        var forged = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(manifest.Database, "saas_platform", StringComparison.Ordinal));
        Assert.Throws<TenantContractException>(() => BackupManifestCodec.Verify(forged, key, manifest.TenantId));
        Assert.Throws<TenantContractException>(() => BackupManifestCodec.Verify(bytes, RandomNumberGenerator.GetBytes(32), manifest.TenantId));
    }

    [Fact]
    public void Alpha_backup_cannot_be_selected_for_Delta()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Assert.Throws<TenantContractException>(() => BackupManifestCodec.Verify(BackupManifestCodec.Sign(Backup(), key), key, Guid.NewGuid()));
    }

    [Fact]
    public void Backup_rejects_mixed_or_partial_chains_and_missing_identity()
    {
        var source = Backup();
        Assert.Throws<TenantContractException>(() => (source with { Parts = [source.Parts[0], source.Parts[0]] }).Validate(source.TenantId));
        Assert.Throws<TenantContractException>(() => (source with { TenantId = Guid.Empty }).Validate(source.TenantId));
        Assert.Throws<TenantContractException>(() => (source with { Database = "saas_platform" }).Validate(source.TenantId));
        Assert.Throws<TenantContractException>(() => (source with { Parts = [source.Parts[0] with { Position = 2 }] }).Validate(source.TenantId));
    }

    [Fact]
    public void Release_rejects_traversal_duplicate_paths_and_missing_bundle()
    {
        var manifest = Release();
        manifest.Validate();
        Assert.Throws<TenantContractException>(() => (manifest with { Files = [manifest.Files[0] with { Path = "../outside" }] }).Validate());
        Assert.Throws<TenantContractException>(() => (manifest with { Files = [manifest.Files[0], manifest.Files[0] with { Path = "BUNDLES/tenancy" }] }).Validate());
        Assert.Throws<TenantContractException>(() => (manifest with { Files = [] }).Validate());
    }

    [Fact]
    public void Release_rejects_duplicate_migration_owners_and_unordered_migrations()
    {
        var manifest = Release();
        Assert.Throws<TenantContractException>(() => (manifest with { Streams = [manifest.Streams[0], manifest.Streams[0] with { Id = "other" }] }).Validate());
        Assert.Throws<TenantContractException>(() => (manifest with { Streams = [manifest.Streams[0] with { Migrations = ["20261107000000_B", "20261106000000_A"] }] }).Validate());
    }

    [Fact]
    public async Task Artifact_hash_is_checked_before_any_bundle_is_available()
    {
        var home = Path.Combine(Path.GetTempPath(), "saas-m6-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "bundles"));
        Directory.CreateDirectory(Path.Combine(home, "sql"));
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var bytes = Encoding.UTF8.GetBytes("reviewed payload");
            var manifest = Release() with
            {
                Files = [new("bundles/tenancy", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))),
                    new("sql/tenancy.sql", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))]
            };
            await File.WriteAllBytesAsync(Path.Combine(home, "bundles", "tenancy"), bytes, ct);
            await File.WriteAllBytesAsync(Path.Combine(home, "sql", "tenancy.sql"), bytes, ct);
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest, ReleasePackage.Json);
            await File.WriteAllBytesAsync(Path.Combine(home, "release.json"), json, ct);
            var digest = Convert.ToHexString(SHA256.HashData(json));
            await using (var package = await ReleasePackage.OpenAsync(home, digest, ct))
                Assert.Equal(manifest.SourceSha, package.Manifest.SourceSha);
            await File.WriteAllTextAsync(Path.Combine(home, "bundles", "tenancy"), "altered", ct);
            await Assert.ThrowsAsync<TenantContractException>(() => ReleasePackage.OpenAsync(home, digest, ct));
        }
        finally { Directory.Delete(home, true); }
    }

    private static BackupManifest Backup()
    {
        var id = Guid.NewGuid();
        return new(1, id, Guid.NewGuid(), "saas_t_" + id.ToString("N"), DateTimeOffset.UtcNow,
            [new("database.bak", 1234, new string('A', 64), Guid.NewGuid(), 1)]);
    }
    private static ReleaseManifest Release() => new(1, new string('a', 40), "linux-x64",
        [new("bundles/tenancy", 1, new string('A', 64)), new("sql/tenancy.sql", 1, new string('A', 64))],
        [new("tenancy", "tenant", "TenantDbContext", "tenancy", "bundles/tenancy", "sql/tenancy.sql", ["20261106000000_Baseline"], ["Standard", "Approval"])]);
}
