using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeUsageMonitor;

/// <summary>
/// Reads the installed Claude Code version to build an accurate User-Agent header.
/// Result is cached — claude --version is only executed once per process lifetime.
/// </summary>
internal static partial class ClaudeCodeInfo
{
    private static string? _cachedVersion;

    internal static string UserAgent => $"claude-code/{Version}";

    internal static string Version => _cachedVersion ??= ReadVersion();

    private static string ReadVersion()
    {
        try
        {
            var psi = new ProcessStartInfo("claude", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);
            var match = VersionRegex().Match(output);
            if (match.Success) return match.Value;
        }
        catch { }

        // Fallback if claude binary is not found or version cannot be parsed
        return "2.1.69";
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionRegex();
}
