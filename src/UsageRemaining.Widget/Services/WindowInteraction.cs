using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace UsageRemaining.Widget.Services;

public static class WindowInteraction
{
    public static bool CanDragFrom(DependencyObject? source)
    {
        for (var item = source; item is not null;)
        {
            if (item is ButtonBase or Slider or Thumb or ScrollBar or ScrollViewer or TextBoxBase or PasswordBox or Selector or Hyperlink)
                return false;
            item = item is FrameworkContentElement content ? content.Parent :
                item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) ?? LogicalTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return source is not null;
    }
}
