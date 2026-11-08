using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SaaS.UnitTests;

public sealed class SqlFixtureTests
{
    private const string TestSha = "0123456789abcdef0123456789abcdef01234567";
    private const string PinnedDigest = "mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaasOne.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }

    private static string PowerShell()
    {
        var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, executable))
            .First(File.Exists);
    }

    [Fact]
    public async Task A_successful_run_followed_by_early_pull_failure_cannot_reuse_pass_evidence()
    {
        using var fixture = new FakeSqlFixture();
        var first = await fixture.RunAsync("success");
        Assert.True(first.ExitCode == 0, first.Output);
        using var succeeded = fixture.ReadReport();
        Assert.Equal("PASS", succeeded.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, succeeded.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(TestSha, succeeded.RootElement.GetProperty("sourceSha").GetString());
        Assert.Equal(2, succeeded.RootElement.GetProperty("passed").GetInt32());
        var previousRunId = succeeded.RootElement.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(previousRunId));
        Assert.True(fixture.Exists("sql-smoke.json"));
        Assert.True(fixture.Exists("sql-command.json"));

        var second = await fixture.RunAsync("pull-fail");
        Assert.True(second.ExitCode != 0, second.Output);
        using var failed = fixture.ReadReport();
        Assert.Equal("BLOCKED", failed.RootElement.GetProperty("status").GetString());
        Assert.Equal(second.ExitCode, failed.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(TestSha, failed.RootElement.GetProperty("sourceSha").GetString());
        Assert.NotEqual(previousRunId, failed.RootElement.GetProperty("runId").GetString());
        Assert.Equal(0, failed.RootElement.GetProperty("passed").GetInt32());
        Assert.False(fixture.Exists("sql-smoke.json"));
        Assert.False(fixture.Exists("sql-command.json"));
    }

    [Fact]
    public async Task Cleanup_failure_invalidates_success_evidence_and_reports_nonzero_exit()
    {
        using var fixture = new FakeSqlFixture();
        var result = await fixture.RunAsync("cleanup-fail");
        Assert.True(result.ExitCode != 0, result.Output);
        using var failed = fixture.ReadReport();
        Assert.Equal("FAIL", failed.RootElement.GetProperty("status").GetString());
        Assert.Equal(result.ExitCode, failed.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(17, failed.RootElement.GetProperty("cleanupExitCode").GetInt32());
        Assert.Equal(TestSha, failed.RootElement.GetProperty("sourceSha").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failed.RootElement.GetProperty("runId").GetString()));
        Assert.Equal(0, failed.RootElement.GetProperty("passed").GetInt32());
        Assert.False(fixture.Exists("sql-smoke.json"));
        Assert.False(fixture.Exists("sql-command.json"));
    }

    [Fact]
    public async Task Trusted_local_SQL_image_skips_registry_pull_and_records_verified_digest()
    {
        using var fixture = new FakeSqlFixture();
        var result = await fixture.RunAsync("cached-pull-fail");
        Assert.True(result.ExitCode == 0, result.Output);
        using var report = fixture.ReadReport();
        Assert.Equal("PASS", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(PinnedDigest, report.RootElement.GetProperty("digest").GetString());
        Assert.Contains("verified cached SQL image", result.Output);
        Assert.False(fixture.Pulled);
    }

    [Fact]
    public async Task Image_not_cached_is_pulled_and_digest_verified_before_SQL()
    {
        using var fixture = new FakeSqlFixture();
        var result = await fixture.RunAsync("uncached-pull");
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(fixture.Pulled);
        using var report = fixture.ReadReport();
        Assert.Equal("PASS", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(PinnedDigest, report.RootElement.GetProperty("digest").GetString());
    }

    [Fact]
    public async Task Untrusted_cached_image_fails_closed_without_running_SQL_smoke()
    {
        using var fixture = new FakeSqlFixture();
        var result = await fixture.RunAsync("untrusted-cache");
        Assert.True(result.ExitCode != 0, result.Output);
        using var report = fixture.ReadReport();
        Assert.Equal("BLOCKED", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, report.RootElement.GetProperty("passed").GetInt32());
        Assert.False(fixture.Pulled);
        Assert.False(fixture.Exists("sql-smoke.json"));
        Assert.False(fixture.Exists("sql-command.json"));
    }

    private sealed class FakeSqlFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "saas-m0-fixture-" + Guid.NewGuid().ToString("N"));
        private readonly string binaries;

        public FakeSqlFixture()
        {
            binaries = Path.Combine(root, "bin");
            Directory.CreateDirectory(binaries);
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.Copy(
                Path.Combine(RepositoryRoot(), "scripts", "sql-fixture.ps1"),
                Path.Combine(root, "scripts", "sql-fixture.ps1"));
            File.Copy(
                Path.Combine(RepositoryRoot(), "scripts", "sql-image.ps1"),
                Path.Combine(root, "scripts", "sql-image.ps1"));
            File.WriteAllText(Path.Combine(root, "scripts", "check.ps1"), CheckScript);

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(binaries, "docker.cmd"), DockerWindows.ReplaceLineEndings("\r\n"));
                File.WriteAllText(Path.Combine(binaries, "git.cmd"), "@echo off\r\necho " + TestSha + "\r\n");
            }
            else
            {
                WriteExecutable("docker", DockerPosix);
                WriteExecutable("git", "#!/bin/sh\necho " + TestSha + "\n");
            }
        }

        public bool Exists(string name) => File.Exists(Path.Combine(root, "artifacts", "verification", name));
        public bool Pulled => File.Exists(Path.Combine(binaries, "image-pulled.marker"));

        public JsonDocument ReadReport() =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts", "verification", "sql-fixture.json")));

        public async Task<(int ExitCode, string Output)> RunAsync(string mode)
        {
            var start = new ProcessStartInfo(PowerShell())
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in new[] { "-NoProfile", "-File", "scripts/sql-fixture.ps1" })
                start.ArgumentList.Add(arg);
            start.Environment["FAKE_DOCKER_MODE"] = mode;
            start.Environment["FAKE_DOCKER_PULL_MARKER"] = Path.Combine(binaries, "image-pulled.marker");
            start.Environment["PATH"] = binaries + Path.PathSeparator + start.Environment["PATH"];
            start.Environment.Remove("GITHUB_ACTIONS");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start fixture.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); throw; }
            return (process.ExitCode, await stdout + await stderr);
        }

        private void WriteExecutable(string name, string content)
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("POSIX shim cannot run on Windows.");
            var path = Path.Combine(binaries, name);
            File.WriteAllText(path, content);
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private const string CheckScript = """
            param([string]$Scope)
            if ($Scope -ne 'SqlSmoke') { exit 55 }
            if ($env:SAAS_SQL_SMOKE_CONNECTION -match 'Initial Catalog=master') { exit 1 }
            if ($env:SAAS_SQL_SMOKE_CONNECTION -match 'Initial Catalog=saas_m0_smoke') {
                New-Item artifacts/verification -ItemType Directory -Force | Out-Null
                [ordered]@{ status = 'PASS'; sourceSha = '0123456789abcdef0123456789abcdef01234567' } |
                    ConvertTo-Json | Set-Content artifacts/verification/sql-smoke.json
                [ordered]@{ status = 'PASS'; exitCode = 0 } |
                    ConvertTo-Json | Set-Content artifacts/verification/sql-command.json
                exit 0
            }
            exit 2
            """;

        private const string DockerPosix = """
            #!/bin/sh
            case "$1" in
              pull)
                if [ "$FAKE_DOCKER_MODE" = "pull-fail" ] || [ "$FAKE_DOCKER_MODE" = "cached-pull-fail" ]; then exit 42; fi
                if [ "$FAKE_DOCKER_MODE" = "uncached-pull" ]; then echo acquired > "$FAKE_DOCKER_PULL_MARKER"; fi
                ;;
              run) echo fake-container ;;
              exec) exit 0 ;;
              port) echo 127.0.0.1:31433 ;;
              image)
                if [ "$FAKE_DOCKER_MODE" = "pull-fail" ]; then exit 12; fi
                if [ "$FAKE_DOCKER_MODE" = "uncached-pull" ] && [ ! -f "$FAKE_DOCKER_PULL_MARKER" ]; then exit 12; fi
                if [ "$FAKE_DOCKER_MODE" = "untrusted-cache" ]; then
                  echo mcr.microsoft.com/mssql/server@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                else
                  echo mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718
                fi
                ;;
              rm)
                if [ "$FAKE_DOCKER_MODE" = "cleanup-fail" ]; then exit 17; fi
                ;;
              *) exit 44 ;;
            esac
            exit 0
            """;

        private const string DockerWindows = """
            @echo off
            if "%~1"=="pull" (
                if "%FAKE_DOCKER_MODE%"=="pull-fail" exit 42
                if "%FAKE_DOCKER_MODE%"=="cached-pull-fail" exit 42
                if "%FAKE_DOCKER_MODE%"=="uncached-pull" echo acquired>"%FAKE_DOCKER_PULL_MARKER%"
                exit /b 0
            )
            if "%~1"=="run" (echo fake-container & exit /b 0)
            if "%~1"=="exec" exit /b 0
            if "%~1"=="port" (echo 127.0.0.1:31433 & exit /b 0)
            if "%~1"=="image" (
                if "%FAKE_DOCKER_MODE%"=="pull-fail" exit 12
                if "%FAKE_DOCKER_MODE%"=="uncached-pull" (
                    if not exist "%FAKE_DOCKER_PULL_MARKER%" exit 12
                )
                if "%FAKE_DOCKER_MODE%"=="untrusted-cache" (
                    echo mcr.microsoft.com/mssql/server@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                ) else (
                    echo mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718
                )
                exit /b 0
            )
            if "%~1"=="rm" (
                if "%FAKE_DOCKER_MODE%"=="cleanup-fail" exit 17
                exit /b 0
            )
            exit /b 44
            """;
    }
}
