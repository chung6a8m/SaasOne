using System.Diagnostics;
using System.Text.Json;
using Xunit;
namespace SaaS.UnitTests;
public sealed class ScriptTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaasOne.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }
    private static string Pwsh()
    {
        var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, executable)).First(File.Exists);
    }
    private static async Task<(int Code, string Output)> Check(string scope, bool emptyPath = false, bool clearSql = false)
    {
        var start = new ProcessStartInfo(Pwsh()) { WorkingDirectory = Root(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-NoProfile", "-File", "scripts/check.ps1", "-Scope", scope }) start.ArgumentList.Add(argument);
        if (emptyPath) start.Environment["PATH"] = Path.GetTempPath();
        if (clearSql) start.Environment.Remove("SAAS_SQL_SMOKE_CONNECTION");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
    [Fact]
    public async Task Valid_prerequisites_return_zero() => Assert.Equal(0, (await Check("Prerequisites")).Code);
    [Fact]
    public async Task Missing_dotnet_is_not_silently_skipped()
    {
        var result = await Check("Prerequisites", emptyPath: true);
        Assert.NotEqual(0, result.Code);
        Assert.Contains("MissingPrerequisite: dotnet", result.Output);
    }
    [Fact]
    public async Task Missing_sql_secret_is_blocked_not_skipped()
    {
        var result = await Check("SqlSmoke", clearSql: true);
        Assert.NotEqual(0, result.Code);
        Assert.Contains("BLOCKED", result.Output);
    }
    [Fact]
    public async Task Failed_sql_probe_cannot_retain_stale_pass_evidence()
    {
        var evidence = Path.Combine(Root(), "artifacts", "verification");
        Directory.CreateDirectory(evidence);
        var stale = Path.Combine(evidence, "sql-smoke.json");
        File.WriteAllText(stale, "{\"status\":\"PASS\",\"sourceSha\":\"stale\"}");
        var result = await Check("SqlSmoke", clearSql: true);
        Assert.NotEqual(0, result.Code);
        Assert.False(File.Exists(stale));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "sql-command.json")));
        Assert.Equal("BLOCKED", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, report.RootElement.GetProperty("exitCode").GetInt32());
    }
}
