using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static void CheckBackgroundOpacity(MainWindow window)
    {
        var child = new Border();
        var parent = new Border { Child = child };
        Check(BackgroundOpacity.GetValue(child) == 1, "background opacity defaults to fully opaque");
        BackgroundOpacity.SetValue(parent, .75);
        Check(BackgroundOpacity.GetValue(child) == .75, "background opacity inherits through the visual tree");
        foreach (var (input, expected) in new[] { (-1d, .55), (2d, 1d), (double.NaN, 1d), (double.PositiveInfinity, 1d) })
        {
            BackgroundOpacity.SetValue(parent, input);
            Check(BackgroundOpacity.GetValue(child) == expected, "background opacity coerces invalid or out-of-range values");
        }

        var slider = (Slider)window.FindName("OpacitySlider");
        var text = (TextBlock)window.FindName("PrimaryPercent");
        var card = Ancestor<Border>(text);
        var opaqueText = AlphaPixels(text);
        foreach (var factor in new[] { .55, .75, 1d })
        {
            slider.Value = factor;
            window.UpdateLayout();
            Check(window.Opacity == 1 && BackgroundOpacity.GetValue(window) == factor,
                $"{factor:P0} changes the background factor while the main window stays opaque");
            CheckAlpha(RenderPixels(window), 10, 200, factor, "main gradient interior");
            CheckAlpha(RenderPixels(card), 3, card.ActualHeight / 2, factor, "quota card interior");
            CheckAlpha(RenderPixels(card), 0, card.ActualHeight / 2, 1, "quota card border");
            Check(AlphaPixels(text).SequenceEqual(opaqueText) && opaqueText.Max() == 255,
                $"quota text remains fully opaque at {factor:P0}");
            if (factor == .55) Capture(window, "widget-background-55");
        }
        CheckStyledControlOpacity();
        slider.Value = .75;
    }

    private static void CheckStyledControlOpacity()
    {
        var button = new Button { Width = 100, Height = 40, Content = "M", Background = Brushes.Navy,
            BorderBrush = Brushes.Red, BorderThickness = new Thickness(1) };
        var buttonHost = OpacityHost(button, 100, 40);
        foreach (var factor in new[] { .55, .75, 1d })
        {
            BackgroundOpacity.SetValue(buttonHost, factor);
            buttonHost.UpdateLayout();
            CheckAlpha(RenderPixels(button), 10, 20, factor, "button fill");
            CheckAlpha(RenderPixels(button), 0, 20, 1, "button outline");
        }
        button.IsEnabled = false;
        var disabledAlpha = RenderPixels(button).Alpha(0, 20);
        BackgroundOpacity.SetValue(buttonHost, .55);
        Check(RenderPixels(button).Alpha(0, 20) == disabledAlpha && disabledAlpha is > 0 and < 255,
            "disabled button semantic dimming is preserved independently of background opacity");

        var progress = new ProgressBar { Width = 100, Height = 8, Minimum = 0, Maximum = 100, Value = 50 };
        var progressHost = OpacityHost(progress, 100, 8);
        var check = new CheckBox { IsChecked = true };
        var checkHost = OpacityHost(check, 30, 24);
        var slider = new Slider { Width = 100, Height = 24, Minimum = 0, Maximum = 1, Value = .5 };
        var sliderHost = OpacityHost(slider, 100, 24);
        var scroll = new ScrollBar { Width = 6, Height = 100, Orientation = Orientation.Vertical,
            Minimum = 0, Maximum = 100, ViewportSize = 25 };
        var scrollHost = OpacityHost(scroll, 6, 100);
        var checkBody = (Border)check.Template.FindName("CheckBorder", check);
        var sliderThumb = Descendants<Thumb>(slider).Single();
        var scrollThumb = Descendants<Thumb>(scroll).Single();
        var stable = new[] { (Element: (FrameworkElement)checkBody, Host: checkHost, Name: "checked control"),
            (Element: (FrameworkElement)VisualTreeHelper.GetChild(sliderThumb, 0), Host: sliderHost, Name: "slider thumb"),
            (Element: (FrameworkElement)VisualTreeHelper.GetChild(scrollThumb, 0), Host: scrollHost, Name: "scroll thumb") };
        foreach (var item in stable)
        {
            var baseline = AlphaPixels(item.Element);
            Check(baseline.Max() > 0, item.Name + " renders visible pixels");
            foreach (var factor in new[] { .55, .75, 1d })
            {
                BackgroundOpacity.SetValue(item.Host, factor);
                Check(AlphaPixels(item.Element).SequenceEqual(baseline), $"{item.Name} retains its original alpha at {factor:P0}");
            }
        }
        foreach (var factor in new[] { .55, .75, 1d })
        {
            BackgroundOpacity.SetValue(progressHost, factor);
            CheckAlpha(RenderPixels(progress), 10, 4, 1, "filled progress indicator");
            CheckAlpha(RenderPixels(progress), 90, 4, factor, "unfilled progress track");
        }
    }

    private static void CheckRegeneratedCalendarOpacity(MainWindow window)
    {
        var slider = (Slider)window.FindName("OpacitySlider");
        slider.Value = .55;
        var panel = (UniformGrid)window.FindName("DaysPanel");
        var oldDay = panel.Children.OfType<Button>().ElementAt(15);
        oldDay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        var day = panel.Children.OfType<Button>().Single(b => Equals(b.Tag, oldDay.Tag));
        Check(!ReferenceEquals(day, oldDay) && BackgroundOpacity.GetValue(day) == .55,
            "regenerated calendar days inherit the current background setting");
        var fill = (Border)day.Template.FindName("ButtonFill", day);
        CheckAlpha(RenderPixels(fill), fill.ActualWidth / 2, fill.ActualHeight / 2, .55 * (32d / 255),
            "regenerated selected calendar day fill");
        slider.Value = .75;
    }

    private static void CheckDialogBackgroundOpacity(MainWindow window, ResetConfirmDialog dialog)
    {
        var slider = (Slider)window.FindName("OpacitySlider");
        var label = (TextBlock)dialog.FindName("DialogCreditText");
        var card = Ancestor<Border>(label);
        var baseline = AlphaPixels(label);
        foreach (var factor in new[] { .55, .75, 1d })
        {
            slider.Value = factor;
            dialog.UpdateLayout();
            Check(dialog.Opacity == 1 && window.Opacity == 1 && BackgroundOpacity.GetValue(dialog) == 1 && BackgroundOpacity.GetValue(window) == factor,
                "open confirmation keeps an opaque background while the main setting changes live");
            CheckAlpha(RenderPixels(dialog), 9, dialog.ActualHeight / 2, 1, "confirmation gradient interior");
            CheckAlpha(RenderPixels(card), 3, card.ActualHeight / 2, 1, "confirmation card fill");
            Check(AlphaPixels(label).SequenceEqual(baseline) && baseline.Max() == 255,
                "confirmation text alpha is unchanged by the background setting");
            if (factor == .55) Capture(dialog, "confirmation-background-55");
        }
        slider.Value = .75;
    }

    private static Border OpacityHost(FrameworkElement child, double width, double height)
    {
        var host = new Border { Child = child, Width = width, Height = height };
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        child.ApplyTemplate();
        host.UpdateLayout();
        return host;
    }

    private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        throw new InvalidOperationException("Expected visual ancestor " + typeof(T).Name);
    }

    private sealed record PixelBuffer(int Width, int Height, byte[] Bytes)
    {
        internal byte Alpha(double x, double y) => Bytes[((int)y * Width + (int)x) * 4 + 3];
    }

    private static PixelBuffer RenderPixels(FrameworkElement element)
    {
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        if (element is Window) bitmap.Render(element);
        else
        {
            // An arranged descendant can retain its parent offset. A local viewbox
            // isolates its own bounds, including thumbs positioned along a track.
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                var bounds = new Rect(0, 0, width, height);
                drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, bounds);
            }
            bitmap.Render(visual);
        }
        var bytes = new byte[width * height * 4];
        bitmap.CopyPixels(bytes, width * 4, 0);
        return new PixelBuffer(width, height, bytes);
    }

    private static byte[] AlphaPixels(FrameworkElement element) => RenderPixels(element).Bytes
        .Where((_, index) => index % 4 == 3).ToArray();

    private static void CheckAlpha(PixelBuffer pixels, double x, double y, double factor, string name)
    {
        var actual = pixels.Alpha(x, y);
        var expected = (int)Math.Round(255 * factor);
        Check(Math.Abs(actual - expected) <= 1, $"{name} alpha {actual} matches {expected}");
    }
}
