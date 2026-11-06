using SaaS.Build;
using Xunit;
namespace SaaS.UnitTests;
public sealed class ConfigHygieneTests
{
    [Fact]
    public void Secret_reference_is_not_a_secret() => Assert.Empty(ConfigHygiene.Inspect("appsettings.json", "{\"SecretReference\":\"SAAS_SQL_SMOKE_CONNECTION\"}"));
    [Fact]
    public void Credential_in_connection_string_is_rejected() => Assert.NotEmpty(ConfigHygiene.Inspect("appsettings.json", "Server=example;Database=test;" + "Password=" + "not-a-real-secret"));
    [Fact]
    public void Private_key_material_is_rejected() => Assert.NotEmpty(ConfigHygiene.Inspect("anything.txt", "-----BEGIN " + "PRIVATE KEY-----"));
    [Theory]
    [InlineData(".env")]
    [InlineData("certificate.pfx")]
    [InlineData("src/SaaS.Web/App_Data/settings.json")]
    public void Sensitive_files_are_rejected(string path) => Assert.NotEmpty(ConfigHygiene.Inspect(path, ""));
    [Theory]
    [InlineData("$runtimePassword")]
    [InlineData("N'$runtimePassword'")]
    [InlineData("\"$runtimePassword\"")]
    public void Runtime_interpolation_is_not_a_literal_secret(string value) => Assert.Empty(ConfigHygiene.Inspect("fixture.ps1", "Pass" + "word = " + value));
    [Theory]
    [InlineData("'not-a-real-secret'")]
    [InlineData("N'not-a-real-secret'")]
    [InlineData("\"not-a-real-secret\"")]
    public void Quoting_does_not_hide_a_literal_secret(string value) => Assert.NotEmpty(ConfigHygiene.Inspect("fixture.ps1", "Pass" + "word = " + value));
}
