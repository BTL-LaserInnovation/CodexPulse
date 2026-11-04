using System.IO;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static class StartupChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "startup-state", Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("USAGEREMAINING_DATA_DIR");
        Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", folder);
        try
        {
            var store = new LocalStateStore();
            check(new WidgetState().StartWithWindows && store.Load().StartWithWindows,
                "startup defaults enabled for new settings and absent state file");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "widget-state.json"), """{"AlwaysOnTop":false,"Opacity":0.75}""");
            var state = store.Load();
            check(state.StartWithWindows && !state.AlwaysOnTop && state.Opacity == .75,
                "legacy settings without startup property retain enabled default and existing preferences");
            state.StartWithWindows = false;
            check(store.Save(state) && !store.Load().StartWithWindows,
                "explicit startup opt-out persists across settings reload");
            state.StartWithWindows = true;
            check(store.Save(state) && store.Load().StartWithWindows,
                "startup opt-in persists across settings reload");

            const string updater = @"C:\Users\Fixture User\AppData\Local\CodexPulse\Update.exe";
            var command = StartupRegistrationService.BuildCommand(updater);
            check(command == "\"" + updater + "\" start" && !command.Contains("app-"),
                "startup command quotes stable updater path with spaces and excludes versioned application paths");
            var backend = new FakeStartupStore();
            var service = new StartupRegistrationService(updater, backend);
            check(service.IsAvailable, "fake stable installation makes startup registration available");
            service.Apply(true);
            service.Apply(true);
            check(backend.Command == command && backend.Writes == 1 && backend.Deletes == 0,
                "startup reconciliation writes stable command once and is idempotent");
            backend.Command = "stale-fixture-command";
            service.Apply(true);
            check(backend.Command == command && backend.Writes == 2,
                "startup reconciliation repairs stale command through fake store");
            service.Apply(false);
            service.Apply(false);
            check(backend.Command is null && backend.Deletes == 1,
                "startup opt-out deletes only the service-owned fake registration and is idempotent");
            backend.Failure = new UnauthorizedAccessException("fixture permission failure");
            check(Throws<UnauthorizedAccessException>(() => service.Apply(true)),
                "startup store permission failure reaches caller for visible UI recovery");
            var unavailableBackend = new FakeStartupStore();
            var unavailable = new StartupRegistrationService(null, unavailableBackend);
            check(Throws<InvalidOperationException>(() => unavailable.Apply(true))
                && Throws<InvalidOperationException>(() => unavailable.Apply(false))
                && !unavailable.IsAvailable && unavailableBackend.Reads == 0 && unavailableBackend.Writes == 0 && unavailableBackend.Deletes == 0,
                "unsupported startup location never reads or mutates registration store");
            check(Throws<ArgumentException>(() => StartupRegistrationService.BuildCommand("relative\\Update.exe"))
                && Throws<ArgumentException>(() => StartupRegistrationService.BuildCommand("C:\\fixture\\\"Update.exe"))
                && Throws<ArgumentException>(() => StartupRegistrationService.BuildCommand("C:\\" + new string('a', 255) + "\\Update.exe")),
                "startup command rejects relative quoted and overlong paths");
            check(!new StartupRegistrationService().IsAvailable,
                "isolated data-directory execution disables real startup registration");
        }
        finally { Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", previous); }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }

    internal sealed class FakeStartupStore : IStartupRegistrationStore
    {
        internal string? Command { get; set; }
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal int Deletes { get; private set; }
        internal Exception? Failure { get; set; }
        public string? Read() { Reads++; if (Failure is not null) throw Failure; return Command; }
        public void Write(string command) { Writes++; if (Failure is not null) throw Failure; Command = command; }
        public void Delete() { Deletes++; if (Failure is not null) throw Failure; Command = null; }
    }

    internal sealed class FakeStartupService : IStartupRegistrationService
    {
        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason { get; set; } = "Fixture startup location unavailable";
        internal Exception? Failure { get; set; }
        internal bool FailOnce { get; set; }
        internal List<bool> Attempts { get; } = [];
        internal bool? Applied { get; private set; }
        public void Apply(bool enabled)
        {
            Attempts.Add(enabled);
            if (Failure is not null)
            {
                var error = Failure;
                if (FailOnce) Failure = null;
                throw error;
            }
            Applied = enabled;
        }
    }
}
