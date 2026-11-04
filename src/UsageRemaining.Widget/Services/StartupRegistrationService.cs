using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Velopack.Locators;

namespace UsageRemaining.Widget.Services;

public interface IStartupRegistrationService
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    void Apply(bool enabled);
}

public interface IStartupRegistrationStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    public const string ValueName = "BTL.CodexPulse";
    private readonly IStartupRegistrationStore registrationStore;
    private readonly string? command;
    public bool IsAvailable => command is not null;
    public string? UnavailableReason { get; }

    public StartupRegistrationService()
    {
        registrationStore = new RunRegistrationStore();
        if (Environment.GetEnvironmentVariable("USAGEREMAINING_DATA_DIR") is not null)
        {
            UnavailableReason = "미리 보기에서는 Windows 로그인 자동 실행을 변경하지 않습니다.";
            return;
        }
        try
        {
            var updateExecutable = FindInstalledUpdater();
            if (updateExecutable is null)
            {
                UnavailableReason = "설치된 Codex Pulse에서 Windows 로그인 자동 실행을 설정할 수 있습니다.";
                return;
            }
            command = BuildCommand(updateExecutable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UnavailableReason = "자동 실행을 설정할 수 없습니다: " + ex.Message;
            Trace.TraceWarning("Startup registration is unavailable: {0}", ex);
        }
    }

    // A supplied store allows verification without touching the user's Run key.
    public StartupRegistrationService(string? updateExecutable, IStartupRegistrationStore registrationStore)
    {
        ArgumentNullException.ThrowIfNull(registrationStore);
        this.registrationStore = registrationStore;
        if (updateExecutable is null)
            UnavailableReason = "설치된 Codex Pulse에서 Windows 로그인 자동 실행을 설정할 수 있습니다.";
        else
            command = BuildCommand(updateExecutable);
    }

    public static string BuildCommand(string updateExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updateExecutable);
        if (!Path.IsPathFullyQualified(updateExecutable) || updateExecutable.Contains('"'))
            throw new ArgumentException("자동 실행 경로가 올바르지 않습니다.", nameof(updateExecutable));
        var result = $"\"{Path.GetFullPath(updateExecutable)}\" start";
        if (result.Length > 260)
            throw new ArgumentException("자동 실행 경로가 Windows의 260자 제한을 초과합니다.", nameof(updateExecutable));
        return result;
    }

    public void Apply(bool enabled)
    {
        if (command is not { } startupCommand) throw new InvalidOperationException(UnavailableReason);
        var current = registrationStore.Read();
        if (enabled)
        {
            if (!string.Equals(current, startupCommand, StringComparison.Ordinal)) registrationStore.Write(startupCommand);
        }
        else if (current is not null)
            registrationStore.Delete();
    }

    public static void RemoveOnUninstall()
    {
        if (Environment.GetEnvironmentVariable("USAGEREMAINING_DATA_DIR") is not null) return;
        try
        {
            if (FindInstalledUpdater() is not null) new RunRegistrationStore().Delete();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning("Could not remove the application's startup registration during uninstall: {0}", ex);
        }
    }

    private static string? FindInstalledUpdater()
    {
        // Detect the executing installation rather than an injected updater or test locator.
        var locator = VelopackLocator.CreateDefaultForPlatform();
        if (locator.CurrentlyInstalledVersion is null || locator.IsPortable ||
            !string.Equals(locator.AppId, "BTL.CodexPulse", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(locator.RootAppDir) || string.IsNullOrWhiteSpace(locator.UpdateExePath))
            return null;
        var expected = Path.GetFullPath(Path.Combine(locator.RootAppDir, "Update.exe"));
        return string.Equals(expected, Path.GetFullPath(locator.UpdateExePath), StringComparison.OrdinalIgnoreCase) &&
            File.Exists(expected) ? expected : null;
    }

    private sealed class RunRegistrationStore : IStartupRegistrationStore
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public string? Read()
        {
            EnsureRuntimeRegistration();
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is null ? null : value as string ?? string.Empty;
        }

        public void Write(string command)
        {
            EnsureRuntimeRegistration();
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }

        public void Delete()
        {
            EnsureRuntimeRegistration();
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        private static void EnsureRuntimeRegistration()
        {
            if (Environment.GetEnvironmentVariable("USAGEREMAINING_DATA_DIR") is not null)
                throw new InvalidOperationException("미리 보기에서는 Windows 자동 실행을 변경할 수 없습니다.");
        }
    }
}
