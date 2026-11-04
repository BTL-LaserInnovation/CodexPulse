using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static int SingleInstanceHelper(string[] args)
    {
        try
        {
            var index = Array.IndexOf(args, "--single-instance-helper");
            var folder = args[index + 1];
            var tag = args[index + 2];
            var delay = int.Parse(args[index + 3], System.Globalization.CultureInfo.InvariantCulture);
            var probe = args.Contains("--probe");
            var marker = Path.Combine(folder, tag);
            using var coordinator = new SingleInstanceCoordinator();
            var ownsMutex = coordinator.TryAcquireOwnership();
            if (!ownsMutex)
            {
                var elapsed = Stopwatch.StartNew();
                var activated = false;
                do
                {
                    ownsMutex = coordinator.TryAcquireOwnership();
                    if (ownsMutex) break;
                    activated = coordinator.TryActivateExistingInstance(TimeSpan.FromMilliseconds(500));
                    if (!activated) Thread.Sleep(50);
                } while (!activated && elapsed.Elapsed < TimeSpan.FromSeconds(probe ? 1 : 8));
                if (!ownsMutex)
                {
                    WriteInstanceMarker(marker + ".secondary", activated ? "activated" : "unavailable");
                    return Application.Current is null && FindWidgetWindow(Environment.ProcessId) == IntPtr.Zero ? 0 : 3;
                }
            }

            WriteInstanceMarker(marker + ".owner", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (probe) return 4;
            if (args.Contains("--stall-ready") || args.Contains("--stall-reply"))
                return StalledInstanceEndpoint(coordinator.PipeName, folder, marker, args.Contains("--stall-reply"));
            if (delay > 0) Thread.Sleep(delay);
            var activations = 0;
            coordinator.StartListening(_ =>
            {
                // The gate models an App whose main window has not loaded yet.
                if (File.Exists(Path.Combine(folder, "not-ready"))) return Task.FromResult(false);
                var count = Interlocked.Increment(ref activations);
                WriteInstanceMarker(marker + ".activation", count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.FromResult(true);
            });
            WriteInstanceMarker(marker + ".listening", "ready");
            var deadline = Stopwatch.StartNew();
            var stopped = false;
            while (deadline.Elapsed < TimeSpan.FromSeconds(30) && !File.Exists(Path.Combine(folder, "exit")))
            {
                if (!stopped && File.Exists(Path.Combine(folder, "stop-listening")))
                {
                    coordinator.StopListening();
                    stopped = true;
                    WriteInstanceMarker(marker + ".stopped", "still owns mutex");
                }
                Thread.Sleep(20);
            }
            return deadline.Elapsed < TimeSpan.FromSeconds(30) && Application.Current is null ? 0 : 5;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int SingleInstanceChecks()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "single-instance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var children = new List<Process>();
        var previous = Environment.GetEnvironmentVariable("USAGEREMAINING_DATA_DIR");
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Checks apphost unavailable");
            var data = Path.Combine(root, "identity");
            Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", data);
            using (var identity = new SingleInstanceCoordinator())
            using (var equivalent = new SingleInstanceCoordinator(Path.Combine(data, ".")))
            using (var separate = new SingleInstanceCoordinator(Path.Combine(root, "other-identity")))
            {
                Check(identity.MutexName == equivalent.MutexName && identity.PipeName == equivalent.PipeName,
                    "single-instance identity normalizes the isolated data-directory path");
                Check(identity.MutexName != separate.MutexName && identity.PipeName != separate.PipeName,
                    "test data-directory identities cannot activate the real user's widget");
            }

            var copy = Path.Combine(root, "copied-build");
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            var copiedExecutable = Path.Combine(copy, Path.GetFileName(executable));

            Process Start(string folder, string tag, int delay = 0, bool copied = false, bool probe = false, string? identity = null, bool stallReady = false, bool stallReply = false)
            {
                Directory.CreateDirectory(folder);
                var start = new ProcessStartInfo(copied ? copiedExecutable : executable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add("--single-instance-helper");
                start.ArgumentList.Add(folder);
                start.ArgumentList.Add(tag);
                start.ArgumentList.Add(delay.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (probe) start.ArgumentList.Add("--probe");
                if (stallReady) start.ArgumentList.Add("--stall-ready");
                if (stallReply) start.ArgumentList.Add("--stall-reply");
                start.Environment["USAGEREMAINING_DATA_DIR"] = identity ?? data;
                var child = Process.Start(start) ?? throw new InvalidOperationException("Unable to start instance helper");
                children.Add(child);
                return child;
            }

            var normal = Path.Combine(root, "normal");
            var owner = Start(normal, "owner");
            WaitForInstanceFile(Path.Combine(normal, "owner.listening"), owner);
            var secondary = Start(normal, "secondary", copied: true);
            WaitForInstanceExit(secondary);
            Check(File.ReadAllText(Path.Combine(normal, "secondary.secondary")) == "activated" && !owner.HasExited,
                "copied executable activates the existing owner and exits successfully");
            WaitForInstanceFile(Path.Combine(normal, "owner.activation"), owner);
            Check(!File.Exists(Path.Combine(normal, "secondary.owner")) && !File.Exists(Path.Combine(data, "widget-state.json")),
                "secondary launch creates no extra owner or application state");
            Check(FindWidgetWindow(owner.Id) == IntPtr.Zero && FindWidgetWindow(secondary.Id) == IntPtr.Zero,
                "IPC helpers perform ownership and activation without constructing WPF windows");

            var other = Path.Combine(root, "parallel-identity");
            var independent = Start(other, "owner", identity: Path.Combine(root, "other-identity"));
            WaitForInstanceFile(Path.Combine(other, "owner.listening"), independent);
            Check(!independent.HasExited && !owner.HasExited,
                "different isolated identities can own their independent mutexes concurrently");
            File.WriteAllText(Path.Combine(other, "exit"), "close");
            WaitForInstanceExit(independent);

            // During shutdown/update teardown, stopping IPC must not surrender ownership early.
            File.WriteAllText(Path.Combine(normal, "stop-listening"), "update teardown");
            WaitForInstanceFile(Path.Combine(normal, "owner.stopped"), owner);
            var duringShutdown = Start(normal, "shutdown-probe", probe: true);
            WaitForInstanceExit(duringShutdown);
            Check(File.ReadAllText(Path.Combine(normal, "shutdown-probe.secondary")) == "unavailable" && !owner.HasExited,
                "shutdown handoff retains exclusive ownership until the old process exits");
            var handoffFolder = Path.Combine(root, "waiting-handoff");
            var handoff = Start(handoffFolder, "owner", copied: true);
            Thread.Sleep(150);
            Check(!handoff.HasExited && !File.Exists(Path.Combine(handoffFolder, "owner.owner")),
                "launch during owner teardown waits without creating a second owner");
            File.WriteAllText(Path.Combine(normal, "exit"), "close");
            WaitForInstanceExit(owner);
            WaitForInstanceFile(Path.Combine(handoffFolder, "owner.listening"), handoff);
            Check(!handoff.HasExited, "waiting launch acquires ownership after the old owner exits during its retry");
            File.WriteAllText(Path.Combine(handoffFolder, "exit"), "close");
            WaitForInstanceExit(handoff);
            var replacementFolder = Path.Combine(root, "replacement");
            var replacement = Start(replacementFolder, "owner", copied: true);
            WaitForInstanceFile(Path.Combine(replacementFolder, "owner.listening"), replacement);
            Check(!replacement.HasExited, "normal close allows a replacement executable to acquire ownership");
            File.WriteAllText(Path.Combine(replacementFolder, "exit"), "close");
            WaitForInstanceExit(replacement);

            var delayed = Path.Combine(root, "startup");
            var startupOwner = Start(delayed, "owner", delay: 700);
            WaitForInstanceFile(Path.Combine(delayed, "owner.owner"), startupOwner);
            File.WriteAllText(Path.Combine(delayed, "not-ready"), "waiting for main window");
            var early = Start(delayed, "early");
            Thread.Sleep(1000);
            Check(!early.HasExited && !File.Exists(Path.Combine(delayed, "owner.activation")),
                "launch before the listener and UI are ready waits for activation acceptance");
            File.Delete(Path.Combine(delayed, "not-ready"));
            WaitForInstanceExit(early);
            WaitForInstanceFile(Path.Combine(delayed, "owner.activation"), startupOwner);
            Check(File.ReadAllText(Path.Combine(delayed, "early.secondary")) == "activated",
                "startup activation is delivered after the existing UI becomes ready");
            startupOwner.Kill(entireProcessTree: true);
            startupOwner.WaitForExit(5000);
            var recoveredFolder = Path.Combine(root, "recovered");
            var recovered = Start(recoveredFolder, "owner");
            WaitForInstanceFile(Path.Combine(recoveredFolder, "owner.listening"), recovered);
            Check(!recovered.HasExited, "crashed owner and abandoned mutex permit immediate recovery");
            File.WriteAllText(Path.Combine(recoveredFolder, "exit"), "close");
            WaitForInstanceExit(recovered);

            foreach (var reply in new[] { false, true })
            {
                var stalledFolder = Path.Combine(root, reply ? "stalled-ack" : "stalled-ready");
                var stalled = Start(stalledFolder, "owner", stallReady: !reply, stallReply: reply);
                WaitForInstanceFile(Path.Combine(stalledFolder, "owner.listening"), stalled);
                using (var caller = new SingleInstanceCoordinator())
                {
                    var elapsed = Stopwatch.StartNew();
                    var activated = caller.TryActivateExistingInstance(TimeSpan.FromMilliseconds(350));
                    Check(!activated && elapsed.Elapsed < TimeSpan.FromSeconds(2), reply
                        ? "connected endpoint that never acknowledges activation is bounded by the client timeout"
                        : "connected endpoint that never sends its handshake is bounded by the client timeout");
                    Console.WriteLine($"IPC stalled {(reply ? "ACK" : "READY")} deadline: {elapsed.ElapsedMilliseconds} ms");
                }
                WaitForInstanceFile(Path.Combine(stalledFolder, "owner.connected"), stalled);
                File.WriteAllText(Path.Combine(stalledFolder, "exit"), "close");
                WaitForInstanceExit(stalled);
            }

            var race = Path.Combine(root, "race");
            var contenders = Enumerable.Range(0, 6).Select(i => Start(race, "race-" + i, copied: i % 2 == 1)).ToArray();
            WaitForInstanceCondition(() => Directory.EnumerateFiles(race, "*.secondary").Count() == 5,
                "simultaneous secondary launches complete", TimeSpan.FromSeconds(12));
            var winners = Directory.GetFiles(race, "*.owner");
            var winnerId = int.Parse(File.ReadAllText(winners.Single()), System.Globalization.CultureInfo.InvariantCulture);
            foreach (var loser in contenders.Where(process => process.Id != winnerId)) WaitForInstanceExit(loser);
            Check(winners.Length == 1 && contenders.Count(p => !p.HasExited) == 1,
                "six simultaneous launches from two build directories elect exactly one live owner");
            Check(Directory.GetFiles(race, "*.secondary").All(path => File.ReadAllText(path) == "activated"),
                "all launch-race losers activate the elected owner");
            File.WriteAllText(Path.Combine(race, "exit"), "close");
            foreach (var contender in contenders) WaitForInstanceExit(contender);
            Check(Application.Current is null && !File.Exists(Path.Combine(data, "widget-state.json")),
                "noninteractive single-instance suite never initializes App or writes widget state");
            Console.WriteLine($"All {passed} single-instance checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); }
                child.Dispose();
            }
            Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", previous);
        }
    }

    private static void WaitForInstanceFile(string file, Process owner) =>
        WaitForInstanceCondition(() =>
        {
            if (owner.HasExited)
                throw new InvalidOperationException($"Instance helper exited early ({owner.ExitCode}): {owner.StandardError.ReadToEnd()}");
            return File.Exists(file);
        }, Path.GetFileName(file), TimeSpan.FromSeconds(8));

    private static void WriteInstanceMarker(string path, string value)
    {
        File.WriteAllText(path + ".tmp", value);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static int StalledInstanceEndpoint(string pipeName, string folder, string marker, bool reply)
    {
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accept = new Thread(() =>
        {
            try
            {
                pipe.WaitForConnection();
                if (reply)
                {
                    pipe.WriteByte(1);
                    if (pipe.ReadByte() != 2) throw new IOException("Stalled endpoint received an unexpected activation command");
                }
                WriteInstanceMarker(marker + ".connected", reply ? "intentionally withholding ACK" : "intentionally withholding READY");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Debug.WriteLine("Stalled endpoint cleanup: " + ex.Message);
            }
        }) { IsBackground = true };
        accept.Start();
        WriteInstanceMarker(marker + ".listening", "ready");
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15) && !File.Exists(Path.Combine(folder, "exit"))) Thread.Sleep(20);
        return elapsed.Elapsed < TimeSpan.FromSeconds(15) ? 0 : 5;
    }

    private static void WaitForInstanceExit(Process process)
    {
        if (!process.WaitForExit(12_000)) throw new TimeoutException("Instance helper did not exit within its deadline");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Instance helper failed ({process.ExitCode}): {process.StandardError.ReadToEnd()}");
    }

    private static void WaitForInstanceCondition(Func<bool> condition, string name, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed >= timeout) throw new TimeoutException("Timed out waiting for " + name);
            Thread.Sleep(20);
        }
    }
}
