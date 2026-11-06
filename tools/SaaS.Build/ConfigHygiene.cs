using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SaaS.Build;

public static class ConfigHygiene
{
    public static IReadOnlyList<string> Inspect(string path, string content)
    {
        var errors = new List<string>();
        var normalized = "/" + path.Replace('\\', '/');
        var name = Path.GetFileName(path);
        if (name == ".env" || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || new[] { ".pfx", ".p12", ".key" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            || normalized.Contains("/App_Data/", StringComparison.OrdinalIgnoreCase)) errors.Add("Sensitive file must not be tracked.");
        if (Regex.IsMatch(content, @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")) errors.Add("Private key material.");
        if (Regex.IsMatch(content, "(?i)(?:password|pwd)\\s*=\\s*[^\\s;\\\"'$<{][^;\\r\\n]*")) errors.Add("Literal credential assignment.");
        if (Regex.IsMatch(content, @"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,})\b")) errors.Add("Access token material.");
        return errors;
    }

    public static void ValidateRepository(string root)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("-z");
        using var process = Process.Start(start) ?? throw new InvalidDataException("Cannot enumerate tracked files.");
        var files = process.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || files.Length == 0) throw new InvalidDataException("Git file inventory failed: " + error);
        var findings = new List<string>();
        foreach (var file in files)
        {
            var fullPath = Path.Combine(root, file);
            if (!File.Exists(fullPath)) throw new InvalidDataException("Tracked file missing: " + file);
            foreach (var finding in Inspect(file, File.ReadAllText(fullPath))) findings.Add(file + ": " + finding);
        }
        if (findings.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, findings));
    }
}
