namespace SaaS.AccessControl.Data;

public sealed class AclState
{
    public int Id { get; set; } = 1;
    public Guid TenantId { get; set; }
    public long SecurityGeneration { get; set; }
    public long Revision { get; set; }
    public Guid AppliedOperationId { get; set; }
    public string AclHash { get; set; } = "";
}
public sealed class RoleRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}
public sealed class RoleGrantRow
{
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = "";
    public PermissionEffect Effect { get; set; }
}
public sealed class UserRoleRow
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}
public sealed class UserGrantRow
{
    public Guid UserId { get; set; }
    public string Permission { get; set; } = "";
    public PermissionEffect Effect { get; set; }
}
public sealed class AclHistory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public long SecurityGeneration { get; set; }
    public long Revision { get; set; }
    public string Payload { get; set; } = "";
    public string AclHash { get; set; } = "";
    public DateTimeOffset ArchivedAt { get; set; }
}
