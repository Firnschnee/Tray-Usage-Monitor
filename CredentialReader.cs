using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>
/// Reads the OAuth Access Token from Claude Code.
///
/// Search paths (in order):
///   1. Windows Credential Manager: "Claude Code-credentials"
///   2. %USERPROFILE%\.claude\.credentials.json
///   3. %HOMEDRIVE%%HOMEPATH%\.claude\.credentials.json  (Fallback)
/// </summary>
public static class CredentialReader
{
    private const string CredentialName = "Claude Code-credentials";
    private const string FileName = ".credentials.json";
    private const string DirName = ".claude";

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    /// <summary>
    /// Reads the OAuth Access Token.
    /// </summary>
    public static string? GetAccessToken()
    {
        // --- Attempt 1: Credential Manager ---
        try
        {
            var json = ReadFromCredentialManager();
            if (json != null)
            {
                Log($"Token read from Credential Manager ({json.Length} bytes)");
                var token = ExtractAccessToken(json);
                if (token != null) return token;
                Log("Credential Manager: JSON found but could not extract accessToken");
            }
            else
            {
                Log("Credential Manager: No entry 'Claude Code-credentials' found");
            }
        }
        catch (Exception ex)
        {
            Log($"Credential Manager error: {ex.Message}");
        }

        // --- Attempt 2: File (multiple paths) ---
        foreach (var path in GetCredentialFilePaths())
        {
            try
            {
                if (!File.Exists(path))
                {
                    Log($"File not found: {path}");
                    continue;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                Log($"File read: {path} ({json.Length} bytes)");

                if (!json.Contains("claudeAiOauth"))
                {
                    Log($"File does not contain 'claudeAiOauth': {path}");
                    continue;
                }

                var token = ExtractAccessToken(json);
                if (token != null)
                {
                    Log($"Token extracted from file: {path}");
                    return token;
                }

                Log($"File contains claudeAiOauth but no accessToken: {path}");
            }
            catch (Exception ex)
            {
                Log($"File error ({path}): {ex.Message}");
            }
        }

        Log("NO TOKEN FOUND in any source");
        return null;
    }

    /// <summary>All possible paths for the credentials file.</summary>
    private static IEnumerable<string> GetCredentialFilePaths()
    {
        // Primary: %USERPROFILE%\.claude\.credentials.json
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(userProfile))
            yield return Path.Combine(userProfile, DirName, FileName);

        // Fallback: Environment.SpecialFolder.UserProfile
        var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(specialFolder) && specialFolder != userProfile)
            yield return Path.Combine(specialFolder, DirName, FileName);

        // Fallback: %HOMEDRIVE%%HOMEPATH%
        var homeDrive = Environment.GetEnvironmentVariable("HOMEDRIVE");
        var homePath = Environment.GetEnvironmentVariable("HOMEPATH");
        if (!string.IsNullOrEmpty(homeDrive) && !string.IsNullOrEmpty(homePath))
        {
            var combined = homeDrive + homePath;
            if (combined != userProfile && combined != specialFolder)
                yield return Path.Combine(combined, DirName, FileName);
        }
    }

    private static string? ReadFromCredentialManager()
    {
        if (!CredReadW(CredentialName, 1, 0, out var credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize <= 0) return null;

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);

            // Try UTF-8 (default)
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("claudeAiOauth")) return text;

            // UTF-16 Fallback
            text = Encoding.Unicode.GetString(bytes);
            if (text.Contains("claudeAiOauth")) return text;

            Log($"Credential Manager: Blob read ({cred.CredentialBlobSize} bytes) but no claudeAiOauth found");
            return null;
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    private static string? ExtractAccessToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.TryGetProperty("accessToken", out var token))
            {
                var val = token.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    Log($"accessToken extracted ({val.Length} chars)");
                    return val;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"JSON parse error: {ex.Message}");
        }
        return null;
    }

    private static void Log(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[CredentialReader] {msg}");
    }
}
