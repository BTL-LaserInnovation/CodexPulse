using System.Windows;
using System.Windows.Input;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class GoogleActionConfirmDialog : Window
{
    public GoogleActionConfirmDialog(Window owner, string heading, string description, string confirmText)
    {
        InitializeComponent();
        Owner = owner;
        BackgroundOpacity.SetValue(this, 1);
        HeadingText.Text = heading;
        DescriptionText.Text = description;
        ConfirmButton.Content = confirmText;
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Dialog_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && WindowInteraction.CanDragFrom(e.OriginalSource as DependencyObject)) DragMove();
    }
}
