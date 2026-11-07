namespace SaaS.AccessControl;

public enum PermissionEffect { Unspecified = 0, Allow = 1, Deny = 2 }

/// <summary>The pure permission leg; credential, tenant and resource authority remain mandatory at the service boundary.</summary>
public static class PermissionPolicy
{
    public static bool Allows(IEnumerable<PermissionEffect> roles, PermissionEffect user, bool resourceSatisfied) => false;
}
