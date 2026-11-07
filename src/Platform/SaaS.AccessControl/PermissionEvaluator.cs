namespace SaaS.AccessControl;

public sealed class PermissionEvaluator(PermissionCatalog catalog)
{
    public AccessTicket Require(CurrentAccess actor, AccessSnapshot snapshot, string permission, ResourceDescriptor? resource = null)
    {
        var definition = catalog.Get(permission);
        if (actor.UserId == Guid.Empty || actor.TenantId == Guid.Empty || actor.RegistryVersion < 1 || actor.SecurityGeneration < 1)
            throw new AccessDeniedException(401, "credential_invalid");
        if (actor.TenantId != snapshot.TenantId) throw new AccessDeniedException(403, "tenant_mismatch");
        if (snapshot.SecurityGeneration != actor.SecurityGeneration || snapshot.Revision < 1)
            throw new AccessDeniedException(503, "access_revalidation_required");
        try { catalog.Validate(snapshot.Document); }
        catch (AccessDeniedException) { throw new AccessDeniedException(503, "access_state_invalid"); }
        var resourceValid = resource is null ? definition.ResourceRule == ResourceRule.None
            : resource.TenantId == actor.TenantId && !string.IsNullOrWhiteSpace(resource.Id) && resource.Id.Length <= 256
              && !resource.Id.Any(char.IsControl) && (definition.ResourceRule != ResourceRule.Owner || resource.OwnerId == actor.UserId);
        if (!actor.Entitlements.Contains(definition.Entitlement) || !resourceValid || !Effective(snapshot.Document, actor.UserId, permission))
            throw new AccessDeniedException(403, "permission_denied");
        return new(actor.TenantId, actor.UserId, actor.SecurityGeneration, actor.RegistryVersion, snapshot.Revision, permission);
    }

    internal static bool Effective(AclDocument document, Guid user, string permission)
    {
        var roleIds = document.Assignments.Where(x => x.UserId == user).Select(x => x.RoleId).ToHashSet();
        var roles = document.Roles.Where(x => roleIds.Contains(x.Id)).SelectMany(x => x.Permissions)
            .Where(x => x.Permission == permission).Select(x => x.Effect);
        var userEffect = document.Overrides.SingleOrDefault(x => x.UserId == user && x.Permission == permission)?.Effect ?? PermissionEffect.Unspecified;
        return PermissionPolicy.Allows(roles, userEffect, true);
    }
}

/// <summary>Must run again inside the serialized ACL transaction, never solely in middleware.</summary>
public sealed class AclAdministrationGuard(PermissionCatalog catalog)
{
    public void Demand(CurrentAccess actor, AccessSnapshot current, AclDocument proposed)
    {
        _ = new PermissionEvaluator(catalog).Require(actor, current, "Access.Manage");
        catalog.Validate(proposed);
        var before = current.Document;
        var ownRoles = before.Assignments.Where(x => x.UserId == actor.UserId).ToHashSet();
        var ownAfter = proposed.Assignments.Where(x => x.UserId == actor.UserId).ToHashSet();
        if (!ownRoles.SetEquals(ownAfter)
            || !before.Overrides.Where(x => x.UserId == actor.UserId).ToHashSet().SetEquals(proposed.Overrides.Where(x => x.UserId == actor.UserId)))
            throw new AccessDeniedException(403, "self_administration_forbidden");
        static string Fingerprint(RoleSpec role) => new AclDocument([role], [], []).Hash();
        var oldRoles = before.Roles.ToDictionary(x => x.Id);
        var newRoles = proposed.Roles.ToDictionary(x => x.Id);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in oldRoles.Keys.Union(newRoles.Keys))
        {
            oldRoles.TryGetValue(id, out var oldRole);
            newRoles.TryGetValue(id, out var newRole);
            if (oldRole is not null && newRole is not null && Fingerprint(oldRole) == Fingerprint(newRole)) continue;
            if (ownRoles.Any(x => x.RoleId == id)) throw new AccessDeniedException(403, "self_administration_forbidden");
            foreach (var grant in (oldRole?.Permissions ?? []).Concat(newRole?.Permissions ?? [])) affected.Add(grant.Permission);
        }
        foreach (var assignment in before.Assignments.Except(proposed.Assignments).Concat(proposed.Assignments.Except(before.Assignments)))
        {
            foreach (var role in new[] { oldRoles.GetValueOrDefault(assignment.RoleId), newRoles.GetValueOrDefault(assignment.RoleId) })
                if (role is not null) foreach (var grant in role.Permissions) affected.Add(grant.Permission);
        }
        foreach (var change in before.Overrides.Except(proposed.Overrides).Concat(proposed.Overrides.Except(before.Overrides))) affected.Add(change.Permission);
        foreach (var permission in affected)
        {
            var definition = catalog.Get(permission);
            if (!definition.Delegable || !actor.Entitlements.Contains(definition.Entitlement) || !PermissionEvaluator.Effective(before, actor.UserId, permission))
                throw new AccessDeniedException(403, "permission_not_delegable");
        }
    }
}
