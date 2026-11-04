using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static int UiChecks(bool fixture)
    {
        var folder = Environment.GetEnvironmentVariable("USAGEREMAINING_DATA_DIR") ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ui", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", folder);
        var log = Path.Combine(folder, "fake-consumes.log");
        var signal = Path.Combine(folder, "switch-account.signal");
        if (fixture)
        {
            System.Net.Http.HttpClient.DefaultProxy = new System.Net.WebProxy("http://127.0.0.1:9");
            Environment.SetEnvironmentVariable("CODEX_EXECUTABLE", Environment.ProcessPath);
            Environment.SetEnvironmentVariable("CODEX_PULSE_CHECK_UI", "1");
            Environment.SetEnvironmentVariable("CODEX_PULSE_CHECK_LOG", log);
            Environment.SetEnvironmentVariable("CODEX_PULSE_CHECK_SIGNAL", signal);
            var now = DateTimeOffset.UtcNow;
            new LocalStateStore(folder).Save(new WidgetState
            {
                PublicCalendar = new(now, now,
                [new(now.AddDays(-2), DateOnly.FromDateTime(now.AddDays(-2).UtcDateTime), "regular", "fixture public history", "https://example.invalid"),
                 new(now.AddDays(-1), DateOnly.FromDateTime(now.AddDays(-1).UtcDateTime), "banked", "fixture grant history", "https://example.invalid"),
                 new(now.AddDays(-1), DateOnly.FromDateTime(now.AddDays(-1).UtcDateTime), "banked", "fixture grant preannouncement", "https://example.invalid", IsAnnouncement: true)], "Fixture only", PublicResetCalendar.ProviderId)
            });
        }
        var app = new App();
        if (fixture) app.Startup += (_, _) => CheckQueuedStartupActivation(app);
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
                await WaitUntilAsync(() => ((TextBlock)window.FindName("StatusText")).Text == "Codex 연결됨", "widget connects", 20_000);
                if (fixture) Check(!AppActivationPending(app), "queued duplicate activation drains after the main window loads");
                if (!fixture)
                    await WaitUntilAsync(() => ((TextBlock)window.FindName("PublicStatusText")).Text.Contains("건"), "public reset history visible in live calendar", 20_000);
                Check(((System.Windows.Controls.Primitives.UniformGrid)window.FindName("DaysPanel")).Children.Count == 42, "42-cell month calendar");
                var legend = (StackPanel)window.FindName("CalendarLegend");
                Check(legend.Children.OfType<TextBlock>().Select(t => t.Text).SequenceEqual(new[] { "리셋", "리셋권", "예고", "내 예정" }),
                    "calendar legend distinguishes resets credits preannouncements and personal schedule");
                Check(legend.ActualWidth >= legend.DesiredSize.Width && legend.TranslatePoint(new Point(legend.ActualWidth, 0), window).X <= window.ActualWidth,
                    "four calendar categories fit compact window without clipping");
                Check(((TextBlock)window.FindName("PublicStatusText")).ToolTip?.ToString()?.Contains("OpenTheRank") == true,
                    "calendar source is visibly attributed to OpenTheRank");
                var pin = (CheckBox)window.FindName("TopmostCheck");
                pin.IsChecked = false; Check(!window.Topmost, "topmost off");
                pin.IsChecked = true; Check(window.Topmost, "topmost on");
                var opacity = (Slider)window.FindName("OpacitySlider");
                opacity.Value = .75; Check(window.Opacity == 1 && Math.Abs(BackgroundOpacity.GetValue(window) - .75) < .001, "live background-only opacity control");
                CheckBackgroundOpacity(window);
                CheckUpdateButton(window);
                await VerifyTrayAsync(window);
                Check(WindowInteraction.CanDragFrom((TextBlock)window.FindName("PrimaryPercent")) &&
                    !WindowInteraction.CanDragFrom((System.Windows.Shapes.Path)window.FindName("CollapseIcon")) &&
                    !WindowInteraction.CanDragFrom(opacity), "usage background drags but interactive control descendants do not");
                Check(((TextBlock)window.FindName("AccountText")).Text.Contains('@'), "verified account email visible");
                Check(window.Icon is BitmapFrame icon && icon.Decoder.Frames.Select(f => f.PixelWidth)
                    .SequenceEqual(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }), "window icon resource retains all nine resolutions");
                Check(((Image)window.FindName("AppLogo")).Source is BitmapSource { PixelWidth: > 0 }, "selected C artwork is loaded in main header");
                CheckQuotaCardLayout(window, "Primary");
                CheckQuotaCardLayout(window, "Secondary");
                CheckCreditColumns(window);
                var panel = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("DaysPanel");
                ((Button)panel.Children[15]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                CheckRegeneratedCalendarOpacity(window);
                Check(((TextBlock)window.FindName("SelectedDateText")).Text.Contains("현지 시간"), "calendar dates use PC local timezone");
                if (fixture) CheckCalendarSelection(window);
                var selected = panel.Children.OfType<Button>().FirstOrDefault(b => b.ToolTip?.ToString()?.Contains("내 ") == true);
                selected?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (fixture)
                {
                    var credits = (StackPanel)window.FindName("CreditList");
                    Check(credits.Children.Count == 3 && Descendants<Button>(credits).First().Tag?.ToString() == "fixture-earliest", "visible reset credits sorted earliest expiry first");
                    Check(((TextBlock)window.FindName("CreditsTitle")).Text.StartsWith("리셋권 5개") &&
                        ((TextBlock)window.FindName("CreditsHint")).Visibility == Visibility.Collapsed,
                        "partial detail notice preserves authoritative count without overlapping credit rows");
                    Check(new LocalStateStore(folder).Load().CodexExecutable == Environment.ProcessPath, "redemption UI test is bound to fake executable only");
                    var use = Descendants<Button>(credits).First();
                    use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var dialog = await WaitForDialogAsync();
                    Check(dialog.Opacity == 1 && window.Opacity == 1 && BackgroundOpacity.GetValue(dialog) == BackgroundOpacity.GetValue(window) && dialog.Owner == window,
                        "confirmation matches main background opacity and owner");
                    CheckDialogBackgroundOpacity(window, dialog);
                    await VerifyTrayModalAsync(window, dialog);
                    Check(dialog.Icon is BitmapFrame { Decoder.Frames.Count: 9 } &&
                        ((Image)dialog.FindName("DialogLogo")).Source is BitmapSource { PixelWidth: > 0 }, "confirmation uses the same C icon identity");
                    var cancel = (Button)dialog.FindName("CancelResetButton");
                    var confirm = (Button)dialog.FindName("ConfirmResetButton");
                    Check(Grid.GetColumn(cancel) > Grid.GetColumn(confirm) && cancel.IsDefault && cancel.IsCancel && !confirm.IsDefault && cancel.IsKeyboardFocused,
                        "cancel is on right and has safe default keyboard focus");
                    Capture(dialog, "confirmation-fixture");
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => Descendants<Button>(credits).FirstOrDefault()?.IsEnabled == true, "cancel restores credit actions");
                    Check(!File.Exists(log), "cancelling confirmation consumes no credit");
                    use = Descendants<Button>(credits).First();
                    use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialog = await WaitForDialogAsync();
                    ((Button)dialog.FindName("ConfirmResetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => ((TextBlock)window.FindName("CreditsTitle")).Text.StartsWith("리셋권 4개"), "successful fake use refreshes credit balance");
                    Check(File.ReadAllLines(log).SequenceEqual(new[] { "fixture-earliest" }), "confirmation and duplicate click consume exactly selected fake credit once");
                    Check(new LocalStateStore(folder).Load().PendingResetUses.Count == 0, "successful response clears pending idempotency record");
                    CheckCreditColumns(window);
                    await WaitUntilAsync(() => Descendants<Button>(credits).FirstOrDefault()?.IsEnabled == true, "credit actions ready for account-switch guard");
                    Descendants<Button>(credits).First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialog = await WaitForDialogAsync();
                    File.WriteAllText(signal, "switch fake account");
                    await WaitUntilAsync(() => !((Button)dialog.FindName("ConfirmResetButton")).IsEnabled, "account notification invalidates open confirmation");
                    Capture(dialog, "confirmation-account-changed-fixture");
                    ((Button)dialog.FindName("CancelResetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => ((TextBlock)window.FindName("AccountText")).Text == "other@example.invalid", "account changes refresh displayed identity after cancel");
                    Check(File.ReadAllLines(log).Length == 1, "account change during confirmation sends no additional redemption");
                }
                opacity.Value = 1;
                if (!fixture && new LocalStateStore(folder).Load().PublicCalendar is { } feed &&
                    feed.Events.FirstOrDefault(e => !e.IsScheduled && !e.IsAnnouncement && e.Time is not null) is { Time: { } historyTime } historyEvent)
                {
                    var date = LocalTime.Convert(historyTime).Date;
                    var today = LocalTime.Convert(DateTimeOffset.UtcNow).Date;
                    var monthShift = (date.Year - today.Year) * 12 + date.Month - today.Month;
                    if (monthShift != 0)
                    {
                        var navigate = Descendants<Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == (monthShift < 0 ? "이전 달" : "다음 달"));
                        for (var i = 0; i < Math.Abs(monthShift); i++) navigate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    panel.Children.OfType<Button>().Single(b => b.Tag is DateTime d && d == date).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var expectedLabel = historyEvent.Kind == "banked" ? "리셋권 지급 발표" : "리셋 적용";
                    Check(((TextBlock)window.FindName("DetailText")).Text.Contains(expectedLabel), "live latest public event is readable on its PC-local calendar date");
                    Capture(window, "widget-public-history");
                    Descendants<Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "오늘로 이동").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Capture(window, fixture ? "widget-fixture" : "widget");
                opacity.Value = .55;
                var left = window.Left;
                var top = window.Top;
                var collapse = (Button)window.FindName("CollapseButton");
                collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                var compactHeight = ((FrameworkElement)window.FindName("CompactSecondaryName")).Visibility == Visibility.Visible ? 112 : 84;
                Check(window.Width == 360 && window.Height == compactHeight && ((FrameworkElement)window.FindName("ExpandedPanel")).Visibility == Visibility.Collapsed &&
                    ((FrameworkElement)window.FindName("CompactPanel")).Visibility == Visibility.Visible, "collapse fits actual quota row count without minimizing");
                Check(window.Left == left && window.Top == top, "collapse preserves window position");
                Check(new LocalStateStore(folder).Load().IsCollapsed, "collapsed preference saved immediately");
                Check(window.Opacity == 1 && BackgroundOpacity.GetValue(window) == .55 &&
                    new LocalStateStore(folder).Load().Opacity == .55 && AlphaPixels((TextBlock)window.FindName("CompactPercent")).Max() == 255,
                    "collapsed weekly view keeps opaque text and preserves the background setting");
                CheckAlpha(RenderPixels(window), 10, 42, .55, "collapsed main gradient interior");
                if (fixture)
                {
                    Check(((TextBlock)window.FindName("CompactPercent")).Text == "63%" &&
                        ((TextBlock)window.FindName("CompactSecondaryPercent")).Text == "88%" && window.Height == 112,
                        "collapsed fixture shows weekly 63% and five-hour 88% together");
                    CheckCompactLayout(window, 2);
                }
                Capture(window, fixture ? "widget-collapsed-fixture" : "widget-collapsed");
                if (fixture) CheckCompactTransitions(window);
                pin.IsChecked = false;
                await VerifyTrayRestoreAsync(window);
                pin.IsChecked = true;
                collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.Height == 480 && ((FrameworkElement)window.FindName("ExpandedPanel")).Visibility == Visibility.Visible, "expand restores full view");
                Check(window.Opacity == 1 && BackgroundOpacity.GetValue(window) == .55, "expand preserves background opacity without fading the window");
                var status = ((TextBlock)window.FindName("StatusText")).Text;
                Console.WriteLine($"UI: status={status}; size={window.ActualWidth}x{window.ActualHeight}");
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

    private static void CheckCalendarSelection(MainWindow window)
    {
        var panel = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("DaysPanel");
        var grant = panel.Children.OfType<Button>().Single(b => b.ToolTip?.ToString()?.Contains("리셋권 지급 발표") == true);
        var grantDate = grant.Tag;
        var ordinary = panel.Children.OfType<Button>().First(b => b.Tag is DateTime date &&
            date.Month == LocalTime.Convert(DateTimeOffset.UtcNow).Month &&
            b.Content is Grid grid && !Descendants<System.Windows.Shapes.Ellipse>(grid).Any());
        var ordinaryDate = ordinary.Tag;
        ordinary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        ordinary = panel.Children.OfType<Button>().Single(b => Equals(b.Tag, ordinaryDate));
        var white = ((SolidColorBrush)window.FindResource("TextBrush")).Color;
        var violet = ((SolidColorBrush)window.FindResource("VioletBrush")).Color;
        var sky = ((SolidColorBrush)window.FindResource("SkyBrush")).Color;
        Check(ordinary.BorderBrush is SolidColorBrush ordinaryBorder && ordinaryBorder.Color == white &&
            ordinary.Background is SolidColorBrush ordinaryFill && ordinaryFill.Color == Color.FromArgb(32, 255, 255, 255),
            "ordinary selected date has the white selection outline and subtle fill");
        grant = panel.Children.OfType<Button>().Single(b => Equals(b.Tag, grantDate));
        Check(grant.BorderBrush is SolidColorBrush grantBorder && grantBorder.Color == violet,
            "unselected grant date retains its purple event outline");
        grant.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        grant = panel.Children.OfType<Button>().Single(b => Equals(b.Tag, grantDate));
        var surface = (Border)grant.Template.FindName("ButtonSurface", grant);
        Check(grant.BorderBrush is SolidColorBrush selectedBorder && selectedBorder.Color == white &&
            surface.BorderBrush is SolidColorBrush visibleBorder && visibleBorder.Color == white &&
            grant.Background is SolidColorBrush fill && fill.Color == Color.FromArgb(32, 255, 255, 255),
            "selected grant uses the same white outline and subtle fill as an ordinary selected date");
        var content = (Grid)grant.Content;
        var markers = Descendants<System.Windows.Shapes.Ellipse>(content).ToArray();
        Check(markers.Select(marker => (marker.Fill as SolidColorBrush)?.Color).SequenceEqual(new Color?[] { violet, sky }),
            "selected grant keeps distinct purple grant and blue announcement markers");
        Check(content.ActualHeight <= surface.ActualHeight - surface.BorderThickness.Top - surface.BorderThickness.Bottom + .1 &&
            content.Children.OfType<FrameworkElement>().All(child => child.TranslatePoint(new Point(0, child.ActualHeight), content).Y <= content.ActualHeight + .1),
            "date number and multiple event markers fit inside the calendar cell without clipping");
        Capture(window, "widget-calendar-selection-fixture");
    }

    private static void CheckQuotaCardLayout(MainWindow window, string prefix)
    {
        window.UpdateLayout();
        var reset = (TextBlock)window.FindName(prefix + "Reset");
        var progress = (ProgressBar)window.FindName(prefix + "Progress");
        var text = new FormattedText(reset.Text, System.Globalization.CultureInfo.CurrentCulture, reset.FlowDirection,
            new Typeface(reset.FontFamily, reset.FontStyle, reset.FontWeight, reset.FontStretch), reset.FontSize,
            reset.Foreground, VisualTreeHelper.GetDpi(reset).PixelsPerDip)
        { LineHeight = reset.LineHeight, MaxTextWidth = reset.ActualWidth };
        Check(reset.ActualHeight >= text.Height && reset.ActualHeight >= 26,
            prefix + " reset text has room for two complete lines");
        var bottom = reset.TranslatePoint(new Point(0, reset.ActualHeight), window).Y;
        var top = progress.TranslatePoint(new Point(0, 0), window).Y;
        Check(top - bottom >= 3, prefix + " progress bar is separated from reset text");
    }

    private static void CheckCreditColumns(MainWindow window)
    {
        window.UpdateLayout();
        var list = (StackPanel)window.FindName("CreditList");
        var scroll = (ScrollViewer)window.FindName("CreditScroll");
        var rows = list.Children.OfType<Grid>().ToList();
        if (rows.Count == 0) return;
        Check(rows.All(row => row.Children.Count == 4 && row.Children.OfType<TextBlock>().All(t => !t.Text.Contains('\n')) &&
            row.ActualHeight <= 24 && row.ActualWidth <= scroll.ViewportWidth + .1),
            "credit rows stay single-line inside the scroll viewport");
        var first = rows[0];
        Check(rows.All(row => Enumerable.Range(1, 3).All(column =>
                Math.Abs(((FrameworkElement)row.Children[column]).TranslatePoint(new Point(), list).X -
                         ((FrameworkElement)first.Children[column]).TranslatePoint(new Point(), list).X) < .1)),
            "expiry countdown and action align across different credit names");
        Check(rows.All(row => row.Children[0] is TextBlock name && name.TextTrimming == TextTrimming.CharacterEllipsis &&
            name.ToolTip is string fullName && !string.IsNullOrWhiteSpace(fullName)),
            "credit names have constrained width ellipsis and full-name tooltips");
    }

    private static async Task WaitUntilAsync(Func<bool> ready, string name, int milliseconds = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.ElapsedMilliseconds < milliseconds) await Task.Delay(40);
        Check(ready(), name);
    }

    private static async Task<ResetConfirmDialog> WaitForDialogAsync()
    {
        await WaitUntilAsync(() => Application.Current.Windows.OfType<ResetConfirmDialog>().Any(), "confirmation opens");
        await Task.Delay(50);
        return Application.Current.Windows.OfType<ResetConfirmDialog>().Single();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        foreach (var scale in new[] { 1.0, 1.5 })
        {
            var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory("artifacts");
            using var output = File.Create(Path.Combine("artifacts", $"{name}-{scale:0.0}x.png"));
            encoder.Save(output);
        }
    }
}
