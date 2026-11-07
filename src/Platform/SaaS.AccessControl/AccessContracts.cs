using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SaaS.AccessControl;

public enum ResourceRule { None = 0, Owner = 1, Any = 2 }
public sealed record PermissionDefinition(string Code, string Entitlement, ResourceRule ResourceRule = ResourceRule.None, bool Delegable = true);
public sealed record PermissionGrant(string Permission, PermissionEffect Effect);
public sealed record RoleSpec(Guid Id, string Name, PermissionGrant[] Permissions);
public sealed record RoleAssignment(Guid UserId, Guid RoleId);
public sealed record UserOverride(Guid UserId, string Permission, PermissionEffect Effect);
public sealed record AclDocument(RoleSpec[] Roles, RoleAssignment[] Assignments, UserOverride[] Overrides)
{
    public static AclDocument Empty => new([], [], []);
    public string CanonicalJson() => JsonSerializer.Serialize(new AclDocument(
        Roles.OrderBy(x => x.Id).Select(x => x with { Permissions = x.Permissions.OrderBy(p => p.Permission, StringComparer.Ordinal).ToArray() }).ToArray(),
        Assignments.OrderBy(x => x.UserId).ThenBy(x => x.RoleId).ToArray(),
        Overrides.OrderBy(x => x.UserId).ThenBy(x => x.Permission, StringComparer.Ordinal).ToArray()));
    public string Hash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson())));
}
public sealed record CurrentAccess(Guid TenantId, Guid UserId, long SecurityGeneration, long RegistryVersion, IReadOnlySet<string> Entitlements);
public sealed record AccessSnapshot(Guid TenantId, long SecurityGeneration, long Revision, AclDocument Document);
public sealed record ResourceDescriptor(Guid TenantId, Guid OwnerId, string Id);
public sealed record AccessTicket(Guid TenantId, Guid UserId, long SecurityGeneration, long RegistryVersion, long AclRevision, string Permission);
public sealed class AccessDeniedException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
