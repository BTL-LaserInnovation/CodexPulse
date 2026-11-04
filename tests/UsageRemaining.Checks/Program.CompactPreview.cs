using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    // These profiles are used only by this executable's fake app-server mode.
    // They never access a real Codex account or alter production preview controls.
    private static object CompactPreviewResult(string profile, DateTimeOffset started)
    {
        object weekly = new { usedPercent = 37, windowDurationMins = 10080, resetsAt = started.AddDays(4).ToUnixTimeSeconds() };
        object? secondary = profile switch
        {
            "weekly" => null,
            "five-hour" => new { usedPercent = 12, windowDurationMins = 300, resetsAt = started.AddHours(4).ToUnixTimeSeconds() },
            "daily" => new { usedPercent = 92, windowDurationMins = 1440, resetsAt = started.AddHours(18).ToUnixTimeSeconds() },
            "unknown" => new { usedPercent = 24, windowDurationMins = (int?)null, resetsAt = (long?)null },
            _ => throw new ArgumentException("Preview profile must be weekly, five-hour, daily, or unknown.")
        };
        return new { rateLimits = new { primary = weekly, secondary } };
    }

    private static int CompactPreview(string profile)
    {
        _ = CompactPreviewResult(profile, DateTimeOffset.UtcNow);
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "compact-preview-state", profile, Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", folder);
        Environment.SetEnvironmentVariable("CODEX_EXECUTABLE", Environment.ProcessPath);
        Environment.SetEnvironmentVariable("CODEX_PULSE_CHECK_UI", "1");
        Environment.SetEnvironmentVariable("CODEX_PULSE_PREVIEW_PROFILE", profile);
        Environment.SetEnvironmentVariable("CODEX_PULSE_CHECK_SIGNAL", null);
        Environment.SetEnvironmentVariable("CODEX_PULSE_CHECK_LOG", null);
        System.Net.Http.HttpClient.DefaultProxy = new System.Net.WebProxy("http://127.0.0.1:9");
        new LocalStateStore(folder).Save(new WidgetState { IsCollapsed = true, Opacity = 1 });

        var app = new App();
        app.InitializeComponent();
        app.StartupUri = new Uri("/CodexPulse;component/MainWindow.xaml", UriKind.Relative);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var exitCode = 0;
        var started = false;
        app.Activated += async (_, _) =>
        {
            if (started || app.MainWindow is not MainWindow window) return;
            started = true;
            try
            {
                await WaitUntilAsync(() => ((TextBlock)window.FindName("StatusText")).Text == "Codex 연결됨", "fake preview account connects", 20_000);
                await WaitUntilAsync(() => ((TextBlock)window.FindName("CompactPercent")).Text == "63%", "fake preview values render");
                Check(((TextBlock)window.FindName("AccountText")).Text == "test@example.invalid", "preview uses visibly fake account identity");
                Check(new LocalStateStore(folder).Load().CodexExecutable == Environment.ProcessPath, "preview is bound to checks fake executable");
                Check(((Image)window.FindName("AppLogo")).Source is BitmapSource { PixelWidth: > 0 }, "preview retains actual C logo and application styling");
                var compact = (Grid)window.FindName("CompactPanel");
                compact.ToolTip = "FAKE PREVIEW · test@example.invalid\n" + compact.ToolTip;
                CheckCompactLayout(window, profile == "weekly" ? 1 : 2);
                if (profile != "weekly")
                {
                    var expected = profile switch { "five-hour" => ("5시간", "88%"), "daily" => ("일일", "8%"), _ => ("사용 한도", "76%") };
                    Check(((TextBlock)window.FindName("CompactSecondaryName")).Text == expected.Item1 &&
                        ((TextBlock)window.FindName("CompactSecondaryPercent")).Text == expected.Item2, "preview secondary label and remaining value");
                }
                Capture(window, "compact-preview-" + profile);
                Console.WriteLine($"PREVIEW: artifacts/compact-preview-{profile}-1.0x.png and -1.5x.png (fake test@example.invalid account)");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally
            {
                try { await VerifyTrayCloseAsync(window); }
                catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; window.Close(); }
                app.Shutdown(exitCode);
            }
        };
        app.Run();
        return exitCode;
    }

    private static void CheckCompactTransitions(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var snapshotField = typeof(MainWindow).GetField("snapshot", flags)!;
        var render = typeof(MainWindow).GetMethod("RenderCompactUsage", flags)!;
        var original = snapshotField.GetValue(window);
        var left = window.Left;
        var top = window.Top;
        var opacity = BackgroundOpacity.GetValue(window);
        var now = DateTimeOffset.UtcNow;
        void Show(params RateWindow[] rows)
        {
            snapshotField.SetValue(window, new UsageSnapshot(now, rows.ToList()));
            render.Invoke(window, [false]);
            window.UpdateLayout();
        }
        TextBlock Text(string name) => (TextBlock)window.FindName(name);
        ProgressBar Bar(string name) => (ProgressBar)window.FindName(name);
        Color ResourceColor(string name) => ((SolidColorBrush)window.FindResource(name)).Color;
        Color BrushColor(Brush brush) => ((SolidColorBrush)brush).Color;
        try
        {
            Show(new RateWindow("primary", 20, 10080, now.AddDays(2)));
            Check(window.Height == 84 && Text("CompactName").Text == "주간" && Text("CompactPercent").Text == "80%", "two-to-one transition retains single weekly quota at 84px");
            CheckCompactLayout(window, 1);

            Show(new RateWindow("primary", 92, 300, now.AddHours(2)), new("secondary", 37, 10080, now.AddDays(4)));
            Check(window.Height == 112 && Text("CompactName").Text == "주간" && Text("CompactPercent").Text == "63%" &&
                Text("CompactSecondaryName").Text == "5시간" && Text("CompactSecondaryPercent").Text == "8%" &&
                Bar("CompactProgress").Value == 63 && Bar("CompactSecondaryProgress").Value == 8,
                "one-to-two transition orders weekly first even when returned as secondary and preserves row values");
            Check(BrushColor(Text("CompactSecondaryPercent").Foreground) == ResourceColor("CoralBrush") &&
                BrushColor(Bar("CompactSecondaryProgress").Foreground) == ResourceColor("CoralBrush"), "severe secondary quota colors both percentage and progress coral");
            CheckCompactLayout(window, 2);

            Show(new RateWindow("primary", 10, 1440, now.AddHours(18)), new("secondary", 24, null, null));
            Check(Text("CompactName").Text == "일일" && Text("CompactSecondaryName").Text == "사용 한도" &&
                Text("CompactSecondaryPercent").Text == "76%", "weekly-absent daily and unknown period keep provider order and actual values");
            CheckCompactLayout(window, 2);

            Show(new RateWindow("primary", 16, 150, null));
            Check(Text("CompactName").Text == "150분" && Text("CompactPercent").Text == "84%" &&
                Text("CompactSecondaryName").Visibility == Visibility.Collapsed &&
                Text("CompactSecondaryPercent").Text.Length == 0 && Bar("CompactSecondaryProgress").Value == 0,
                "custom duration is explicit and removing secondary clears its stale value");
            CheckCompactLayout(window, 1);

            Show();
            Check(window.Height == 84 && Text("CompactPercent").Text.Contains('—') && Bar("CompactProgress").Value == 0 &&
                Text("CompactSecondaryName").Visibility == Visibility.Collapsed &&
                BrushColor(Text("CompactPercent").Foreground) == ResourceColor("MutedBrush"), "one-to-zero transition shows one neutral placeholder without stale quota");
            CheckCompactLayout(window, 1);

            Show(new RateWindow("primary", 37, 10080, now.AddDays(4)), new("secondary", 12, 300, now.AddHours(4)));
            render.Invoke(window, [true]);
            Check(((Grid)window.FindName("CompactPanel")).ToolTip?.ToString()?.Contains("이전 값") == true,
                "stale compact values retain visible tooltip attribution");
            Check(window.Opacity == 1 && BackgroundOpacity.GetValue(window) == opacity && window.Left == left && window.Top == top,
                "quota row transitions preserve opacity and ordinary window position");

            // Use the primary work area explicitly so the assertion is independent of a second monitor.
            var work = SystemParameters.WorkArea;
            Show(new RateWindow("primary", 37, 10080, now.AddDays(4)));
            window.Left = work.Left + 20;
            window.Top = work.Bottom - 84;
            Show(new RateWindow("primary", 37, 10080, now.AddDays(4)), new("secondary", 12, 300, now.AddHours(4)));
            Check(window.Top + window.Height <= work.Bottom + 1 && window.Top >= work.Top,
                "growing compact quota rows clamps the window inside the work area bottom");
        }
        finally
        {
            snapshotField.SetValue(window, original);
            render.Invoke(window, [false]);
            window.Left = left;
            window.Top = top;
            window.UpdateLayout();
        }
    }

    private static void CheckCompactLayout(MainWindow window, int rows)
    {
        window.UpdateLayout();
        var panel = (Grid)window.FindName("CompactPanel");
        Check(window.Width == 360 && window.Height == (rows == 2 ? 112 : 84) && panel.Height == 28 * rows,
            $"{rows}-row compact view uses exact content height");
        Check(panel.ColumnDefinitions.Count == 4 && panel.ColumnDefinitions[0].Width.IsAuto &&
            panel.ColumnDefinitions[1].Width.Value == 64 && panel.ColumnDefinitions[2].Width.IsStar && panel.ColumnDefinitions[3].Width.IsAuto,
            "compact rows share label, fixed percentage, flexible bar and countdown columns");
        var prefixes = rows == 2 ? new[] { "Compact", "CompactSecondary" } : new[] { "Compact" };
        foreach (var prefix in prefixes)
        {
            var bar = (ProgressBar)window.FindName(prefix + "Progress");
            var fields = new[] { "Name", "Percent", "Progress", "Reset" }.Select(s => (FrameworkElement)window.FindName(prefix + s)).ToArray();
            Check(bar.ActualWidth >= 100, prefix + " progress bar remains at least 100 DIP wide");
            Check(fields.All(element =>
            {
                var start = element.TranslatePoint(new Point(), window);
                var end = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), window);
                return element.Visibility == Visibility.Visible && start.X >= 0 && start.Y >= 0 && end.X <= window.ActualWidth + .1 && end.Y <= window.ActualHeight + .1;
            }), prefix + " text and bar remain within complete window bounds");
        }
        if (rows == 2)
        {
            var reset = (FrameworkElement)window.FindName("CompactReset");
            var secondaryReset = (FrameworkElement)window.FindName("CompactSecondaryReset");
            Check(new[] { "Percent", "Progress" }.All(s => Math.Abs(
                ((FrameworkElement)window.FindName("Compact" + s)).TranslatePoint(new Point(), panel).X -
                ((FrameworkElement)window.FindName("CompactSecondary" + s)).TranslatePoint(new Point(), panel).X) < .1) &&
                Math.Abs(reset.TranslatePoint(new Point(reset.ActualWidth, 0), panel).X -
                    secondaryReset.TranslatePoint(new Point(secondaryReset.ActualWidth, 0), panel).X) < .1,
                "percentage, progress and reset columns align across both rows");
        }
        else
            Check(new[] { "Name", "Percent", "Progress", "Reset" }.All(s =>
                ((FrameworkElement)window.FindName("CompactSecondary" + s)).Visibility == Visibility.Collapsed) &&
                panel.RowDefinitions[1].Height.Value == 0, "absent secondary row reserves no visible blank space");
    }
}
