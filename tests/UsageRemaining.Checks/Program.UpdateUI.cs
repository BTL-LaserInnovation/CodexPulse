using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static void CheckUpdateButton(MainWindow window)
    {
        var update = (Button)window.FindName("UpdateButton");
        Check(update.Visibility == Visibility.Collapsed && !update.IsVisible && update.ActualWidth == 0,
            "no available update leaves no visible button or header layout gap");
        var sample = new Button { Content = "Update", Width = 44, Height = 20,
            Style = (Style)window.FindResource("UpdateButtonStyle") };
        var host = OpacityHost(sample, 44, 20);
        var surface = (Border)sample.Template.FindName("UpdateSurface", sample);
        Check(surface.Background is LinearGradientBrush gradient && gradient.GradientStops.Count >= 2
            && gradient.GradientStops.All(stop => stop.Color.A == 255),
            "update style uses opaque multicolor gradient stops");
        foreach (var enabled in new[] { true, false })
        {
            sample.IsEnabled = enabled;
            foreach (var factor in new[] { .55, .75, 1d })
            {
                BackgroundOpacity.SetValue(host, factor);
                CheckAlpha(RenderPixels(sample), 4, 10, 1, "update button fill remains fully opaque");
                Check(sample.Opacity == 1, "update button remains opaque in enabled and busy states");
            }
        }
        Check(!WindowInteraction.CanDragFrom(sample) && !WindowInteraction.CanDragFrom(surface)
            && !WindowInteraction.CanDragFrom(Descendants<TextBlock>(sample).Single()),
            "update button and template descendants never initiate window dragging");
    }

    private static void CheckUpdateHeader(MainWindow window)
    {
        window.UpdateLayout();
        var update = (Button)window.FindName("UpdateButton");
        var left = (StackPanel)update.Parent;
        var title = left.Children.OfType<TextBlock>().Single();
        var grid = (Grid)left.Parent;
        var right = grid.Children.OfType<StackPanel>().Single(panel => Grid.GetColumn(panel) == 1);
        var titleRight = title.TranslatePoint(new Point(title.ActualWidth, 0), window).X;
        var updateLeft = update.TranslatePoint(new Point(0, 0), window).X;
        var updateRight = update.TranslatePoint(new Point(update.ActualWidth, 0), window).X;
        var controlsLeft = right.TranslatePoint(new Point(0, 0), window).X;
        Check(titleRight <= updateLeft && updateRight <= controlsLeft
            && right.TranslatePoint(new Point(right.ActualWidth, 0), window).X <= window.ActualWidth,
            "title update action and right-side controls fit without overlap");
        var previousContent = update.Content;
        foreach (var text in new[] { "Update", "Restart", "Retry", "100%" })
        {
            update.Content = text;
            window.UpdateLayout();
            var label = Descendants<TextBlock>(update).Single();
            var measured = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                label.FlowDirection, new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                label.FontSize, label.Foreground, VisualTreeHelper.GetDpi(label).PixelsPerDip);
            var surface = (Border)update.Template.FindName("UpdateSurface", update);
            var allocated = surface.ActualWidth - surface.BorderThickness.Left - surface.BorderThickness.Right
                - surface.Padding.Left - surface.Padding.Right;
            // A centered label's own rounded width can differ fractionally from its font metrics.
            // Its available content slot determines whether the header action clips.
            Console.WriteLine($"UPDATE LABEL: {text}; measured={measured.WidthIncludingTrailingWhitespace:0.###}; label={label.ActualWidth:0.###}; available={allocated:0.###}");
            Check(measured.WidthIncludingTrailingWhitespace <= allocated + .1,
                "update action label fits fixed header content allocation: " + text);
        }
        update.Content = previousContent;
    }
}
