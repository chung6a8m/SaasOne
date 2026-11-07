using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SaaS.Abstractions.Tenancy;

namespace SaaS.Tenancy.Release;

public sealed record ReleaseFile(string Path, long Length, string Sha256);
public sealed record ReleaseStream(string Id, string Target, string Context, string HistorySchema,
    string Bundle, string Sql, string[] Migrations, string[] Plans);
public sealed record ReleaseManifest(int Version, string SourceSha, string RuntimeIdentifier,
    ReleaseFile[] Files, ReleaseStream[] Streams)
{
    public void Validate()
    {
        if (Version != 1 || SourceSha is null || !Regex.IsMatch(SourceSha, "\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)
            || RuntimeIdentifier is not ("linux-x64" or "win-x64")
            || Files is null || Files.Length is < 1 or > 20000 || Streams is null || Streams.Length is < 1 or > 32)
            throw new TenantContractException("release_manifest_invalid");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null || !ArtifactPath.IsSafe(file.Path) || !paths.Add(file.Path)
                || file.Length < 0 || !Digest.IsSha256(file.Sha256)) throw new TenantContractException("release_inventory_invalid");
        }
        var contexts = new HashSet<string>(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in Streams)
        {
            if (stream is null || string.IsNullOrWhiteSpace(stream.Id) || !ids.Add(stream.Id)
                || stream.Target is not ("platform" or "tenant") || string.IsNullOrWhiteSpace(stream.Context) || !contexts.Add(stream.Context)
                || string.IsNullOrWhiteSpace(stream.HistorySchema) || !owners.Add(stream.Target + "/" + stream.HistorySchema)
                || !ArtifactPath.IsSafe(stream.Bundle) || !paths.Contains(stream.Bundle) || !ArtifactPath.IsSafe(stream.Sql) || !paths.Contains(stream.Sql)
                || stream.Migrations is null || stream.Migrations.Length == 0 || stream.Migrations.Any(string.IsNullOrWhiteSpace)
                || stream.Migrations.Distinct(StringComparer.Ordinal).Count() != stream.Migrations.Length
                || !stream.Migrations.SequenceEqual(stream.Migrations.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || stream.Plans is null || stream.Plans.Length == 0 || stream.Plans.Any(x => x is not ("Standard" or "Approval"))
                || stream.Plans.Distinct(StringComparer.Ordinal).Count() != stream.Plans.Length)
                throw new TenantContractException("release_stream_invalid");
        }
    }
}

public static class Digest
{
    public static bool IsSha256(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);
    public static bool EqualsSha256(string? first, string? second) => IsSha256(first) && IsSha256(second)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first!), Convert.FromHexString(second!));
}

public static class ArtifactPath
{
    public static bool IsSafe(string? path) => !string.IsNullOrEmpty(path) && path.Length <= 512
        && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl)
        && !Path.IsPathRooted(path) && path.Split('/').All(x => x.Length > 0 && x is not ("." or "..") && x == x.Trim() && !x.EndsWith('.'));

    public static string Resolve(string root, string relative)
    {
        if (!IsSafe(relative)) throw new TenantContractException("artifact_path_invalid");
        var current = Path.GetFullPath(root);
        RejectLink(current);
        foreach (var part in relative.Split('/')) { current = Path.Combine(current, part); RejectLink(current); }
        return current;
    }
    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new TenantContractException("artifact_link_forbidden");
    }
}
