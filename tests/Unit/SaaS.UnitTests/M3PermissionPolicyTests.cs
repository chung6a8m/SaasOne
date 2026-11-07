using SaaS.AccessControl;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M3PermissionPolicyTests
{
    public static IEnumerable<object[]> Combinations()
    {
        foreach (var first in Enum.GetValues<PermissionEffect>())
        foreach (var second in Enum.GetValues<PermissionEffect>())
        foreach (var user in Enum.GetValues<PermissionEffect>())
        foreach (var resource in new[] { false, true })
            yield return [first, second, user, resource];
    }
    [Theory]
    [MemberData(nameof(Combinations))]
    public void Deny_is_mandatory_and_unspecified_never_grants(PermissionEffect first, PermissionEffect second, PermissionEffect user, bool resource)
    {
        var expected = resource && (first == PermissionEffect.Allow || second == PermissionEffect.Allow || user == PermissionEffect.Allow)
            && first != PermissionEffect.Deny && second != PermissionEffect.Deny && user != PermissionEffect.Deny;
        Assert.Equal(expected, PermissionPolicy.Allows([first, second], user, resource));
    }
    [Fact]
    public void User_allow_can_grant_without_role_assignment() => Assert.True(PermissionPolicy.Allows([], PermissionEffect.Allow, true));
    [Fact]
    public void Unknown_effect_is_not_treated_as_unspecified()
    {
        Assert.False(PermissionPolicy.Allows([PermissionEffect.Allow, (PermissionEffect)99], PermissionEffect.Unspecified, true));
        Assert.False(PermissionPolicy.Allows([PermissionEffect.Allow], (PermissionEffect)99, true));
    }
}
