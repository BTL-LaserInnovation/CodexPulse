using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static int StartupUiChecks()
    {
        StartupChecks.Run(Check);
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "startup-ui", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", folder);
        var now = DateTimeOffset.UtcNow;
        var fixture = new WidgetState { ActiveAccount = "startup-fixture", Opacity = 1 };
        fixture.Accounts["startup-fixture"] = new()
        {
            Email = "startup-preview@example.invalid", Plan = "plus",
            LastSnapshot = new(now, [new("primary", 37, 10080, now.AddDays(4)), new("secondary", 12, 300, now.AddHours(4))])
        };
        Check(new LocalStateStore().Save(fixture), "isolated startup preview settings saved");
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
                var startup = new StartupChecks.FakeStartupService();
                var update = new UpdateChecks.FakeUpdateService { Offered = new("1.2.1") };
                window = new MainWindow(update, startup) { Left = 150, Top = 150 };
                DetachStartupRuntimeLoading(window);
                window.Show();
                InvokeStartupRender(window, "ApplyStartupPreference");
                var auto = (CheckBox)window.FindName("AutoStartCheck");
                Check(auto.IsChecked == true && auto.IsEnabled && startup.Applied == true,
                    "new startup preference is checked and reconciled using injected fake service");
                auto.IsChecked = false;
                Check(startup.Applied == false && !new LocalStateStore().Load().StartWithWindows,
                    "startup checkbox opt-out updates fake registration and saves immediately");
                auto.IsChecked = true;
                Check(startup.Applied == true && new LocalStateStore().Load().StartWithWindows,
                    "startup checkbox opt-in updates fake registration and saves immediately");
                startup.Failure = new UnauthorizedAccessException("Fixture startup access denied");
                startup.FailOnce = true;
                auto.IsChecked = false;
                Check(auto.IsChecked == true && startup.Applied == true && new LocalStateStore().Load().StartWithWindows,
                    "startup registration failure restores checked preference and leaves saved choice intact");
                Check(((TextBlock)window.FindName("StatusText")).ToolTip?.ToString()?.Contains("Fixture startup access denied") == true,
                    "startup registration failure is visible in existing status detail");
                startup.Failure = null;
                var attempts = startup.Attempts.Count;
                var statePath = Path.Combine(folder, "widget-state.json");
                File.Delete(statePath);
                Directory.CreateDirectory(statePath);
                auto.IsChecked = false;
                Check(auto.IsChecked == true && WindowField<WidgetState>(window, "state").StartWithWindows && startup.Applied == true,
                    "settings write failure rolls back startup checkbox and registration");
                Check(startup.Attempts.Count >= attempts,
                    "settings failure path remains handled without an unobserved UI exception");
                Directory.Delete(statePath);
                Check(new LocalStateStore().Save(WindowField<WidgetState>(window, "state")), "startup UI fixture restored after persistence failure");

                SetWindowField(window, "connected", true);
                InvokeStartupRender(window, "RenderAccount");
                (typeof(MainWindow).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Missing status seam")).Invoke(window, ["Codex 연결됨", true, "Fake startup preview account"]);
                SetWindowField(window, "creditBalance", new ResetCreditBalance(1,
                    [new("fake-preview-credit", "codexRateLimits", "available", now.AddDays(-1), now.AddDays(20), "Fixture reset credit", "Fake preview credit only")], true));
                InvokeStartupRender(window, "RenderUsage");
                InvokeStartupRender(window, "RenderCredits");
                Check(((TextBlock)window.FindName("AccountText")).Text == "startup-preview@example.invalid"
                    && ((TextBlock)window.FindName("PrimaryPercent")).Text == "63%"
                    && ((TextBlock)window.FindName("SecondaryPercent")).Text == "88%",
                    "startup preview uses fake account and weekly 63 percent plus five-hour 88 percent snapshot");
                CheckUpdateButton(window);
                var measurements = new List<string>();
                foreach (var updateVisible in new[] { false, true })
                {
                    if (updateVisible) await WindowField<UpdateCoordinator>(window, "updates").CheckIfDueAsync();
                    foreach (var compact in new[] { false, true })
                    {
                        var state = WindowField<WidgetState>(window, "state");
                        if (state.IsCollapsed != compact)
                            ((Button)window.FindName("CollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        var profile = $"{(compact ? "compact" : "expanded")}-{(updateVisible ? "update" : "normal")}";
                        CheckStartupHeader(window, updateVisible, profile, measurements);
                        if (compact) CheckCompactLayout(window, 2);
                        Capture(window, "startup-header-" + profile);
                    }
                }
                File.WriteAllLines(Path.Combine("artifacts", "startup-header-measurements.txt"), measurements);
                CheckStartupOpacity(window);
                window.Close();
                await WaitUntilAsync(() => WindowField<bool>(window, "closeComplete"), "startup preview window closes cleanly");
                window = null;

                startup = new StartupChecks.FakeStartupService { IsAvailable = false };
                window = new MainWindow(new UpdateChecks.FakeUpdateService(), startup);
                DetachStartupRuntimeLoading(window);
                window.Show();
                InvokeStartupRender(window, "ApplyStartupPreference");
                auto = (CheckBox)window.FindName("AutoStartCheck");
                Check(auto.IsChecked == true && !auto.IsEnabled && startup.Attempts.Count == 0
                    && new LocalStateStore().Load().StartWithWindows,
                    "unavailable startup location preserves saved default with disabled explanatory control and no service writes");
                Check(auto.ToolTip?.ToString()?.Contains("Fixture startup location unavailable") == true,
                    "unavailable startup location explains disabled control");
                window.Close();
                await WaitUntilAsync(() => WindowField<bool>(window, "closeComplete"), "unavailable startup fixture closes cleanly");
                window = null;
                startup = new StartupChecks.FakeStartupService { Failure = new UnauthorizedAccessException("Fixture initial startup denied") };
                window = new MainWindow(new UpdateChecks.FakeUpdateService(), startup);
                DetachStartupRuntimeLoading(window);
                window.Show();
                InvokeStartupRender(window, "ApplyStartupPreference");
                auto = (CheckBox)window.FindName("AutoStartCheck");
                Check(auto.IsChecked is null && !auto.IsEnabled && new LocalStateStore().Load().StartWithWindows,
                    "initial registration failure shows unknown disabled state without losing saved preference");
                Check(auto.ToolTip?.ToString()?.Contains("Fixture initial startup denied") == true,
                    "initial registration failure gives explicit status detail");
                Console.WriteLine($"All {passed} startup checks passed. Preview PNGs and measured bounds are in artifacts.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally
            {
                if (window is not null && !WindowField<bool>(window, "closing")) window.Close();
                app.Shutdown(exitCode);
            }
        }));
        app.Run();
        return exitCode;
    }

    private static void DetachStartupRuntimeLoading(MainWindow window)
    {
        var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing runtime load seam");
        // Keep the real WPF window, resources, rendering, controls and handlers. Detach only
        // account, public HTTP, clock, tray and updater startup work from this fake fixture.
        window.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), window);
    }

    private static void InvokeStartupRender(MainWindow window, string name) =>
        (typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing window render seam " + name)).Invoke(window, null);

    private static Rect StartupBounds(FrameworkElement element, Window window) =>
        new(element.TranslatePoint(new Point(), window), element.RenderSize);

    private static void CheckStartupHeader(MainWindow window, bool updateVisible, string profile, List<string> measurements)
    {
        FrameworkElement Element(string name) => (FrameworkElement)window.FindName(name);
        Rect Bounds(string name) => StartupBounds(Element(name), window);
        var names = new[] { "AppLogo", "AppTitle", "UpdateButton", "AutoStartCheck", "TopmostCheck", "OpacitySlider", "CollapseButton", "CloseButton" };
        var visible = names.Where(name => Element(name).Visibility == Visibility.Visible).ToArray();
        Check(window.ActualWidth == 360 && Element("OpacitySlider").ActualWidth == 36
            && Element("CollapseButton").ActualWidth == 24 && Element("CloseButton").ActualWidth == 24
            && ((TextBlock)Element("AppTitle")).FontSize == 13,
            profile + ": window, slider, buttons and title retain original sizes");
        var header = Bounds("HeaderGrid");
        var body = Bounds(WindowField<WidgetState>(window, "state").IsCollapsed ? "CompactPanel" : "ExpandedPanel");
        Check(Math.Abs(header.Left - 13) < .1 && Math.Abs(header.Right - 347) < .1
            && Math.Abs(body.Left - 21) < .1 && Math.Abs(body.Right - 339) < .1,
            profile + ": header uses 13..347 DIP and body retains 21..339 DIP");
        foreach (var name in visible)
        {
            var bounds = Bounds(name);
            measurements.Add($"{profile} {name} x={bounds.X:0.###} y={bounds.Y:0.###} w={bounds.Width:0.###} h={bounds.Height:0.###}");
            Check(bounds.Left >= 13 - .1 && bounds.Right <= 347 + .1 && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight,
                profile + ": " + name + " fits within window header");
        }
        for (var index = 0; index < visible.Length - 1; index++)
            Check(Bounds(visible[index]).Right <= Bounds(visible[index + 1]).Left + .1,
                profile + ": " + visible[index] + " and " + visible[index + 1] + " do not intersect");
        Check(Bounds("TopmostCheck").Left - Bounds("AutoStartCheck").Right >= 4 - .1,
            profile + ": startup and pin checkbox groups have at least four DIP separation");
        if (updateVisible)
        {
            var gap = Bounds("AutoStartCheck").Left - Bounds("UpdateButton").Right;
            measurements.Add($"{profile} update-to-auto-gap={gap:0.###} DIP");
            Check(gap >= 4 - .1, profile + ": update and startup checkbox have at least four DIP separation");
            CheckUpdateHeader(window);
        }
        else
            Check(Element("UpdateButton").Visibility == Visibility.Collapsed && Element("UpdateButton").ActualWidth == 0
                && WindowInteraction.CanDragFrom(Element("HeaderIdentity")),
                profile + ": absent update reserves no width and blank identity area remains draggable");
        var auto = (CheckBox)Element("AutoStartCheck");
        Check(!WindowInteraction.CanDragFrom(auto) && Descendants<FrameworkElement>(auto).All(child => !WindowInteraction.CanDragFrom(child)),
            profile + ": startup checkbox and all template descendants never drag the window");
    }

    private static void CheckStartupOpacity(MainWindow window)
    {
        var auto = (CheckBox)window.FindName("AutoStartCheck");
        var label = Descendants<TextBlock>(auto).Single();
        var glyph = (Border)auto.Template.FindName("CheckBorder", auto);
        var slider = (Slider)window.FindName("OpacitySlider");
        var baselineLabel = AlphaPixels(label);
        var baselineGlyph = AlphaPixels(glyph);
        foreach (var factor in new[] { .55, .75, 1d })
        {
            slider.Value = factor;
            window.UpdateLayout();
            Check(window.Opacity == 1 && slider.ActualWidth == 36 && Descendants<Thumb>(slider).Single().ActualWidth > 0,
                $"startup header at {factor:P0} preserves full slider width and visible thumb");
            Check(baselineLabel.Max() == 255 && AlphaPixels(label).SequenceEqual(baselineLabel)
                && AlphaPixels(glyph).SequenceEqual(baselineGlyph),
                $"startup label and checked glyph remain opaque at {factor:P0}");
        }
    }
}
