using System.Windows;

namespace UsageRemaining.Widget.Services;

public static class BackgroundOpacity
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(BackgroundOpacity),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.Inherits, null,
            (_, value) => Normalize((double)value)));

    public static double GetValue(DependencyObject element) => (double)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, double value) => element.SetValue(ValueProperty, value);

    internal static double Normalize(double value) => double.IsFinite(value) ? Math.Clamp(value, .55, 1) : 1;
}
