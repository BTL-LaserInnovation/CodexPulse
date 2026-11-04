using System;
using System.IO;
using System.Linq;

namespace UsageRemaining.Widget.Services;

public static class CodexExecutableLocator
{
    public static string? FindExecutable()
    {
        var configured = Normalize(Environment.GetEnvironmentVariable("CODEX_EXECUTABLE"));
        if (IsValidExecutable(configured))
            return configured;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var candidate = Combine(Normalize(directory), "codex.exe");
            if (IsValidExecutable(candidate))
                return candidate;
        }

        var localInstall = Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        if (IsValidExecutable(localInstall))
            return localInstall;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var extensionRoot in new[] { ".vscode", ".vscode-insiders" })
        {
            var extensions = Combine(profile, extensionRoot, "extensions");
            if (extensions is null || !Directory.Exists(extensions))
                continue;

            try
            {
                foreach (var extension in Directory.EnumerateDirectories(extensions, "openai.chatgpt-*")
                             .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var platform in new[] { "windows-x86_64", "windows-aarch64", "win32-x64", "win32-arm64" })
                    {
                        var candidate = Combine(extension, "bin", platform, "codex.exe");
                        if (IsValidExecutable(candidate))
                            return candidate;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An inaccessible optional extension directory must not prevent manual selection.
            }
        }

        return null;
    }

    public static bool IsValidExecutable(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
    }

    private static string? Combine(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        try
        {
            return Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
