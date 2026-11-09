using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class ResetConfirmDialog : Window
{
    private readonly ResetCredit credit;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool invalidated;

    public ResetConfirmDialog(string account, string plan, ResetCredit credit)
    {
        this.credit = credit;
        InitializeComponent();
        BackgroundOpacity.SetValue(this, 1);
        DialogAccountText.Text = account + " · " + plan;
        DialogAccountText.ToolTip = DialogAccountText.Text;
        DialogCreditText.Text = credit.DisplayName;
        DialogExpiryText.Text = credit.ExpiresAt is { } expires ? $"{LocalTime.Convert(expires):yyyy-MM-dd HH:mm:ss} {LocalTime.Label(expires)}" : "만료 시각 미제공";
        DialogDescriptionText.Text = string.IsNullOrWhiteSpace(credit.Description) ? "확인하면 선택한 리셋권 1개가 소모됩니다." : credit.Description;
        DialogStatusText.Text = "확인하면 선택한 리셋권 1개가 소모됩니다.";
        CancelResetButton.IsDefault = true;
        Loaded += (_, _) => { CancelResetButton.Focus(); CheckAvailability(); clock.Start(); };
        clock.Tick += (_, _) => CheckAvailability();
        Closed += (_, _) => clock.Stop();
    }

    public void Invalidate(string reason)
    {
        invalidated = true;
        ConfirmResetButton.IsEnabled = false;
        DialogStatusText.Text = reason;
    }

    private void CheckAvailability()
    {
        if (!credit.IsAvailable(DateTimeOffset.UtcNow)) Invalidate("만료되었거나 사용할 수 없는 리셋권입니다.");
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        CheckAvailability();
        if (!invalidated) DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Dialog_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && WindowInteraction.CanDragFrom(e.OriginalSource as DependencyObject)) DragMove();
    }
}
