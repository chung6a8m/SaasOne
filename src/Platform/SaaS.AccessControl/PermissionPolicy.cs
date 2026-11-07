namespace SaaS.AccessControl;

public enum PermissionEffect { Unspecified = 0, Allow = 1, Deny = 2 }

/// <summary>A mandatory Deny veto; unspecified and malformed effects never grant access.</summary>
public static class PermissionPolicy
{
    public static bool Allows(IEnumerable<PermissionEffect> roles, PermissionEffect user, bool resourceSatisfied)
    {
        if (!resourceSatisfied || !Enum.IsDefined(user) || user == PermissionEffect.Deny) return false;
        var allowed = user == PermissionEffect.Allow;
        foreach (var role in roles)
        {
            if (!Enum.IsDefined(role) || role == PermissionEffect.Deny) return false;
            allowed |= role == PermissionEffect.Allow;
        }
        return allowed;
    }
}
