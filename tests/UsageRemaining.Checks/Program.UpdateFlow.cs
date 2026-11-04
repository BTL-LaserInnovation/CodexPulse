using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static int UpdateUiChecks()
    {
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "update-ui", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", folder);
        var original = new WidgetState { AlwaysOnTop = false, Opacity = .7, Left = 123, Top = 123, IsCollapsed = true,
            ActiveAccount = "fixture-account", CodexExecutable = "C:\\fixture\\codex.exe" };
        original.Accounts["fixture-account"] = new() { Email = "fixture@example.invalid", Plan = "plus" };
        original.PendingResetUses["fixture-account"] = new("fixture-credit", "fixture-operation");
        Check(new LocalStateStore(folder).Save(original), "isolated updater settings fixture saved");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = XDocument.Load(Path.Combine("src", "UsageRemaining.Widget", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = source.Root?.Element(presentation + "Application.Resources")
            ?? throw new InvalidOperationException("Application resource fixture unavailable");
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "services", "clr-namespace:UsageRemaining.Widget.Services;assembly=CodexPulse"),
            resources.Elements().Select(element => new XElement(element)));
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var exitCode = 0;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            MainWindow? window = null;
            try
            {
                var service = new UpdateChecks.FakeUpdateService { Offered = new("1.2.1") };
                // Detach only runtime loading so captures never start Codex or public HTTP work.
                window = new MainWindow(service) { Left = 123, Top = 123 };
                var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Missing runtime load seam");
                window.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), window);
                CheckUpdateButton(window);
                window.Show();
                var coordinator = WindowField<UpdateCoordinator>(window, "updates");
                var button = (Button)window.FindName("UpdateButton");
                await coordinator.CheckIfDueAsync();
                Check(button.Visibility == Visibility.Visible && button.Content?.ToString() == "Update",
                    "available update reveals the header action");
                CheckUpdateHeader(window);
                var collapse = (Button)window.FindName("CollapseButton");
                collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                CheckUpdateHeader(window);
                Capture(window, "update-header-expanded");
                collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                CheckUpdateHeader(window);
                Capture(window, "update-header-compact");

                SetWindowField(window, "creditAction", true);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(coordinator.IsDownloaded && service.Restarts == 0 && !WindowField<bool>(window, "closing"),
                    "reset-credit operation and reconciliation block downloaded update apply");
                SetWindowField(window, "creditAction", false);
                SetWindowField(window, "refreshing", true);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(service.Restarts == 0 && !WindowField<bool>(window, "closing"),
                    "in-flight account refresh blocks downloaded update apply");
                SetWindowField(window, "refreshing", false);
                var dialog = new ResetConfirmDialog("fixture@example.invalid", "Plus",
                    new("fixture-credit", "codexRateLimits", "available", null, null, null, null));
                SetWindowField(window, "resetDialog", dialog);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(service.Restarts == 0 && !WindowField<bool>(window, "closing"),
                    "open reset confirmation blocks downloaded update apply independently of operation flag");
                SetWindowField(window, "resetDialog", null);
                dialog.Close();

                // Simulate a settings persistence failure after the release is downloaded.
                var statePath = Path.Combine(folder, "widget-state.json");
                File.Delete(statePath);
                Directory.CreateDirectory(statePath);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(service.Restarts == 0 && !WindowField<bool>(window, "closing"),
                    "failed settings save prevents update apply and keeps window usable");
                Directory.Delete(statePath);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => service.Restarts == 1 && service.Disposals == 1,
                    "successful explicit update saves settings and prepares graceful restart");
                var saved = new LocalStateStore(folder).Load();
                Check(!saved.AlwaysOnTop && saved.Opacity == .7 && saved.Left == 123 && saved.Top == 123
                    && saved.IsCollapsed && saved.ActiveAccount == original.ActiveAccount
                    && saved.CodexExecutable == original.CodexExecutable
                    && saved.Accounts["fixture-account"].Email == "fixture@example.invalid"
                    && saved.PendingResetUses["fixture-account"] == original.PendingResetUses["fixture-account"],
                    "update shutdown preserves pin opacity position collapse account and reconciliation settings");
                Check(service.Downloads == 1 && service.Applied?.Version == "1.2.1",
                    "blocked apply retries reuse the single completed download");
                Console.WriteLine($"All {passed} updater UI checks passed.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally
            {
                if (window is not null && !WindowField<bool>(window, "closing"))
                {
                    SetWindowField(window, "creditAction", false);
                    SetWindowField(window, "refreshing", false);
                    SetWindowField(window, "resetDialog", null);
                    SetWindowField(window, "updateApplying", false);
                    window.Close();
                }
                app.Shutdown(exitCode);
            }
        }));
        app.Run();
        return exitCode;
    }

    private static T WindowField<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window)
            ?? throw new InvalidOperationException("Missing window test seam " + name));

    private static void SetWindowField(MainWindow window, string name, object? value) =>
        (typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing window test seam " + name)).SetValue(window, value);
}
