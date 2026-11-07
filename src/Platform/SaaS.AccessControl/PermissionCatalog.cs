using System.Collections.Frozen;
using System.Text.RegularExpressions;
namespace SaaS.AccessControl;

/// <summary>Only deployment composition registers permission definitions, never a token or tenant request.</summary>
public sealed class PermissionCatalog
{
    private readonly FrozenDictionary<string, PermissionDefinition> _definitions;
    public PermissionCatalog(IEnumerable<PermissionDefinition> definitions)
    {
        var entries = definitions.ToArray();
        if (entries.Length == 0 || entries.Any(x => x is null || x.Code is null || x.Code.Length > 128
            || !Regex.IsMatch(x.Code, @"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+$", RegexOptions.CultureInvariant)
            || x.Code.StartsWith("OrchardCore.", StringComparison.OrdinalIgnoreCase)
            || x.Code.StartsWith("Platform.", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(x.Entitlement) || x.Entitlement.Length > 64 || !Enum.IsDefined(x.ResourceRule)
            || x.Code == "Access.Manage" && x.Delegable)
            || entries.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new ArgumentException("Invalid deployment permission catalog.", nameof(definitions));
        _definitions = entries.ToFrozenDictionary(x => x.Code, StringComparer.Ordinal);
    }
    public PermissionDefinition Get(string code) => code is not null && _definitions.TryGetValue(code, out var value)
        ? value : throw new AccessDeniedException(403, "permission_unknown");

    public void Validate(AclDocument document)
    {
        void Demand([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid) { if (!valid) throw new AccessDeniedException(400, "acl_invalid"); }
        Demand(document is not null && document.Roles is not null && document.Assignments is not null && document.Overrides is not null);
        var roles = document!.Roles;
        Demand(roles.Length <= 256 && document.Assignments.Length <= 4096 && document.Overrides.Length <= 4096);
        Demand(roles.All(x => x is not null && x.Id != Guid.Empty && !string.IsNullOrWhiteSpace(x.Name)
            && x.Name.Length <= 96 && x.Name == x.Name.Trim() && !x.Name.Any(char.IsControl) && x.Permissions is not null));
        Demand(roles.Select(x => x.Id).Distinct().Count() == roles.Length);
        Demand(roles.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == roles.Length);
        foreach (var role in roles)
        {
            Demand(role.Permissions.Length <= 256);
            Demand(role.Permissions.All(x => x is not null && x.Permission is not null && _definitions.ContainsKey(x.Permission) && Enum.IsDefined(x.Effect)));
            Demand(role.Permissions.Select(x => x.Permission).Distinct(StringComparer.Ordinal).Count() == role.Permissions.Length);
        }
        var ids = roles.Select(x => x.Id).ToHashSet();
        Demand(document.Assignments.All(x => x is not null && x.UserId != Guid.Empty && ids.Contains(x.RoleId)));
        Demand(document.Assignments.Distinct().Count() == document.Assignments.Length);
        Demand(document.Overrides.All(x => x is not null && x.UserId != Guid.Empty && x.Permission is not null && _definitions.ContainsKey(x.Permission) && Enum.IsDefined(x.Effect)));
        Demand(document.Overrides.Select(x => (x.UserId, x.Permission)).Distinct().Count() == document.Overrides.Length);
    }
}
