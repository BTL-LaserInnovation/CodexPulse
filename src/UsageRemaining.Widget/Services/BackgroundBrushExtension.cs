using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace UsageRemaining.Widget.Services;

public sealed class BackgroundBrushExtension : MarkupExtension
{
    public Brush? Brush { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Binding sources hold DependencyObjects weakly; keep inline brushes alive with the binding.
        var binding = new MultiBinding { Converter = BackgroundBrushConverter.Instance, ConverterParameter = Brush, Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding
        {
            Path = new PropertyPath("(0)", BackgroundOpacity.ValueProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.Self)
        });
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class BackgroundBrushConverter : IMultiValueConverter
    {
        internal static readonly BackgroundBrushConverter Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (parameter is not Brush source) return DependencyProperty.UnsetValue;
            var factor = values[0] is double value ? BackgroundOpacity.Normalize(value) : 1;
            // Resource brushes can be shared or frozen; change only this background's copy.
            var brush = source.CloneCurrentValue();
            brush.Opacity = source.Opacity * factor;
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
