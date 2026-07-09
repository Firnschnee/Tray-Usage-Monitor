using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeUsageMonitor;

/// <summary>
/// Reads the installed Claude Code version to build an accurate User-Agent header.
/// Result is cached; claude --version is only executed once per process lifetime.
/// </summary>
internal static partial class ClaudeCodeInfo
{
    private const string FallbackVersion = "2.1.69";

    private static volatile string? _cachedVersion;
    private static int _readStarted;

    internal static string UserAgent => $"claude-code/{Version}";

    /// <summary>
    /// Never blocks the caller: the first access kicks off the version read on
    /// the thread pool and returns the fallback until it completes. Requests made
    /// in that window carry the fallback version; cosmetic, the UA only has to be
    /// plausible.
    /// </summary>
    internal static string Version
    {
        get
        {
            var v = _cachedVersion;
            if (v != null) return v;
            if (Interlocked.Exchange(ref _readStarted, 1) == 0)
                Task.Run(() => _cachedVersion = ReadVersion());
            return FallbackVersion;
        }
    }

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
            var readTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(); } catch { }
                return FallbackVersion;
            }
            var output = readTask.Result.Trim();
            var match = VersionRegex().Match(output);
            if (match.Success) return match.Value;
        }
        catch { }

        // Fallback if claude binary is not found or version cannot be parsed
        return FallbackVersion;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionRegex();
}
