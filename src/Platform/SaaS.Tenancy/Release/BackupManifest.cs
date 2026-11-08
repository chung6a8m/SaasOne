using System.Security.Cryptography;
using System.Text.Json;
using SaaS.Abstractions.Tenancy;
using SaaS.Tenancy.Data;

namespace SaaS.Tenancy.Release;

public sealed record BackupPart(string File, long Length, string Sha256, Guid BackupSetId, int Position);
public sealed record BackupManifest(int Version, Guid TenantId, Guid BackupId, string Database,
    DateTimeOffset CreatedUtc, BackupPart[] Parts)
{
    public void Validate(Guid target)
    {
        if (Version != 1 || target == Guid.Empty || TenantId != target || BackupId == Guid.Empty
            || Database != TenancyConnections.TenantDatabase(target) || CreatedUtc <= DateTimeOffset.UnixEpoch || CreatedUtc.Offset != TimeSpan.Zero
            || Parts is not { Length: 1 } || Parts[0] is not { } part || part.File != "database.bak"
            || part.Length <= 0 || !Digest.IsSha256(part.Sha256) || part.BackupSetId == Guid.Empty || part.Position != 1)
            throw new TenantContractException("backup_manifest_mismatch");
    }
}
public sealed record BackupEnvelope(BackupManifest Manifest, string Authentication);

public static class BackupManifestCodec
{
    public static byte[] Sign(BackupManifest manifest, byte[] key)
    {
        RequireKey(key); manifest.Validate(manifest.TenantId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, ReleasePackage.Json);
        return JsonSerializer.SerializeToUtf8Bytes(new BackupEnvelope(manifest, Convert.ToHexString(HMACSHA256.HashData(key, payload))), ReleasePackage.Json);
    }
    public static BackupManifest Verify(byte[] bytes, byte[] key, Guid target)
    {
        RequireKey(key);
        if (bytes.Length is < 1 or > 64 * 1024) throw new TenantContractException("backup_manifest_invalid");
        BackupEnvelope envelope;
        try { envelope = JsonSerializer.Deserialize<BackupEnvelope>(bytes, ReleasePackage.Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new TenantContractException("backup_manifest_invalid"); }
        if (envelope.Manifest is null) throw new TenantContractException("backup_manifest_invalid");
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope.Manifest, ReleasePackage.Json);
        if (!Digest.EqualsSha256(Convert.ToHexString(HMACSHA256.HashData(key, payload)), envelope.Authentication))
            throw new TenantContractException("backup_manifest_untrusted");
        envelope.Manifest.Validate(target);
        return envelope.Manifest;
    }
    private static void RequireKey(byte[] key)
    {
        if (key is not { Length: 32 }) throw new TenantContractException("backup_signing_key_invalid");
    }
}
