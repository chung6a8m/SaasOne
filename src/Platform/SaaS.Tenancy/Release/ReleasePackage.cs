using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SaaS.Abstractions.Tenancy;

namespace SaaS.Tenancy.Release;

/// <summary>Only a digest obtained independently from the release directory establishes package trust.</summary>
public sealed class ReleasePackage : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true
    };
    private readonly string _root;
    private readonly List<FileStream> _handles = [];
    public ReleaseManifest Manifest { get; private set; } = null!;
    public string ManifestSha256 { get; private set; } = "";
    private ReleasePackage(string root) => _root = Path.GetFullPath(root);

    public static async Task<ReleasePackage> OpenAsync(string root, string trustedDigest, CancellationToken ct)
    {
        if (!Digest.IsSha256(trustedDigest)) throw new TenantContractException("release_trust_required");
        var package = new ReleasePackage(root);
        try
        {
            var manifest = package.Open("release.json");
            if (manifest.Length is < 1 or > 4 * 1024 * 1024) throw new TenantContractException("release_manifest_invalid");
            using var buffer = new MemoryStream();
            await manifest.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            if (!Digest.EqualsSha256(digest, trustedDigest)) throw new TenantContractException("release_manifest_untrusted");
            try { package.Manifest = JsonSerializer.Deserialize<ReleaseManifest>(bytes, Json) ?? throw new JsonException(); }
            catch (JsonException) { throw new TenantContractException("release_manifest_invalid"); }
            package.Manifest.Validate();
            package.ManifestSha256 = digest;
            package.DemandClosedInventory();
            foreach (var item in package.Manifest.Files)
            {
                var stream = package.Open(item.Path);
                if (stream.Length != item.Length || !Digest.EqualsSha256(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)), item.Sha256))
                    throw new TenantContractException("release_artifact_changed");
            }
            return package;
        }
        catch { await package.DisposeAsync(); throw; }
    }

    public string FilePath(string relative)
    {
        if (!Manifest.Files.Any(x => x.Path == relative)) throw new TenantContractException("release_artifact_missing");
        return ArtifactPath.Resolve(_root, relative);
    }

    public async Task VerifyFileAsync(string relative, CancellationToken ct)
    {
        var expected = Manifest.Files.SingleOrDefault(x => x.Path == relative) ?? throw new TenantContractException("release_artifact_missing");
        await using var stream = new FileStream(FilePath(relative), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != expected.Length || !Digest.EqualsSha256(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)), expected.Sha256))
            throw new TenantContractException("release_artifact_changed");
    }
    private void DemandClosedInventory()
    {
        var expected = Manifest.Files.Select(x => x.Path).Append("release.json").ToHashSet(StringComparer.Ordinal);
        var directories = new Stack<string>(); directories.Push(_root);
        while (directories.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new TenantContractException("artifact_link_forbidden");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                else if (!expected.Remove(Path.GetRelativePath(_root, entry).Replace('\\', '/')))
                    throw new TenantContractException("release_unlisted_artifact");
            }
        if (expected.Count != 0) throw new TenantContractException("release_artifact_missing");
    }
    private FileStream Open(string relative)
    {
        var stream = new FileStream(ArtifactPath.Resolve(_root, relative), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        _handles.Add(stream);
        return stream;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var stream in _handles) await stream.DisposeAsync();
        _handles.Clear();
    }
}
