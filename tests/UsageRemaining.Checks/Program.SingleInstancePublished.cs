using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static int SingleInstancePublishedChecks(string executable)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "single-instance-published", Guid.NewGuid().ToString("N"));
        var children = new List<Process>();
        try
        {
            executable = Path.GetFullPath(executable);
            if (!File.Exists(executable)) throw new FileNotFoundException("Publish the widget before its instance smoke check", executable);
            var data = Path.Combine(root, "data");
            var original = new WidgetState { AlwaysOnTop = false, Opacity = .7, Left = 123, Top = 123, IsCollapsed = true, StartWithWindows = false };
            Check(new LocalStateStore(data).Save(original), "published single-instance fixture uses isolated settings");
            var copiedFolder = Path.Combine(root, "copied-build");
            var sourceFolder = Path.GetDirectoryName(executable)!;
            Directory.CreateDirectory(copiedFolder);
            foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copiedFolder, Path.GetRelativePath(sourceFolder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var copiedExecutable = Path.Combine(copiedFolder, Path.GetFileName(executable));

            Process Start(bool copied = false)
            {
                var start = new ProcessStartInfo(copied ? copiedExecutable : executable)
                {
                    UseShellExecute = false, CreateNoWindow = true
                };
                start.Environment["USAGEREMAINING_DATA_DIR"] = data;
                start.Environment["CODEX_EXECUTABLE"] = Environment.ProcessPath!;
                start.Environment["CODEX_PULSE_CHECK_UI"] = "1";
                start.Environment["CODEX_PULSE_CHECK_LOG"] = Path.Combine(root, "fake-consumes.log");
                start.Environment["CODEX_PULSE_CHECK_SIGNAL"] = Path.Combine(root, "unused-signal");
                var process = Process.Start(start) ?? throw new InvalidOperationException("Published widget did not start");
                children.Add(process);
                return process;
            }

            var owner = Start();
            var window = IntPtr.Zero;
            WaitForInstanceCondition(() =>
            {
                if (owner.HasExited) throw new InvalidOperationException("Published owner exited before showing its window");
                window = FindWidgetWindow(owner.Id);
                return window != IntPtr.Zero && TrayRegistered(window);
            }, "published owner window and tray", TimeSpan.FromSeconds(15));
            VerifyNativeTrayBeforeClose(window);
            Check(CountWidgetWindows(owner.Id) == 1, "published owner has one visible widget window");
            foreach (var copied in new[] { false, true, false })
            {
                var secondary = Start(copied);
                if (!secondary.WaitForExit(12_000)) throw new TimeoutException("Published secondary remained running");
                Check(secondary.ExitCode == 0 && !owner.HasExited && FindWidgetWindow(owner.Id) == window,
                    copied ? "copied published build activates the same owner and exits zero" : "repeated published launch activates the same owner and exits zero");
                Check(CountWidgetWindows(secondary.Id) == 0 && CountWidgetWindows(owner.Id) == 1 && TrayRegistered(window),
                    "published secondary creates no extra widget window or owner notification icon");
            }

            ShowWindow(window, 6);
            WaitForInstanceCondition(() => IsIconic(window), "published owner minimizes", TimeSpan.FromSeconds(3));
            var restore = Start(copied: true);
            if (!restore.WaitForExit(12_000)) throw new TimeoutException("Published restore launch remained running");
            WaitForInstanceCondition(() => !IsIconic(window) && IsWindowVisible(window),
                "duplicate launch restores minimized existing widget", TimeSpan.FromSeconds(5));
            Check(restore.ExitCode == 0 && FindWidgetWindow(owner.Id) == window && TrayRegistered(window),
                "duplicate activation restores the original native window with its original tray icon");
            Check(PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero), "published instance accepts normal close");
            if (!owner.WaitForExit(12_000)) throw new TimeoutException("Published owner did not exit after close");
            Check(owner.ExitCode == 0 && !TrayRegistered(window), "normal owner close exits zero and removes its tray icon");
            var saved = new LocalStateStore(data).Load();
            Check(!saved.AlwaysOnTop && saved.Opacity == .7 && saved.IsCollapsed && saved.Left == 123 && saved.Top == 123 && !saved.StartWithWindows,
                "duplicate activation preserves pin opacity collapse position and startup preferences");
            Check(!File.Exists(Path.Combine(root, "fake-consumes.log")), "published duplicate launches consume no reset credits");

            var replacement = Start(copied: true);
            var replacementWindow = IntPtr.Zero;
            WaitForInstanceCondition(() =>
            {
                if (replacement.HasExited) throw new InvalidOperationException("Published replacement exited before startup");
                replacementWindow = FindWidgetWindow(replacement.Id);
                return replacementWindow != IntPtr.Zero && TrayRegistered(replacementWindow);
            }, "replacement published owner", TimeSpan.FromSeconds(15));
            Check(replacement.Id != owner.Id && CountWidgetWindows(replacement.Id) == 1,
                "after full process exit a copied published build becomes the new owner");
            PostMessage(replacementWindow, 0x0010, IntPtr.Zero, IntPtr.Zero);
            if (!replacement.WaitForExit(12_000) || replacement.ExitCode != 0)
                throw new InvalidOperationException("Published replacement did not close cleanly");
            VerifyNativeTrayAfterClose(replacementWindow);
            Console.WriteLine($"All {passed} published single-instance checks passed.");
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
        }
    }

    private static int CountWidgetWindows(int processId)
    {
        var count = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != (uint)processId || !IsWindowVisible(window)) return true;
            var title = new System.Text.StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "Codex Pulse") count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
}
