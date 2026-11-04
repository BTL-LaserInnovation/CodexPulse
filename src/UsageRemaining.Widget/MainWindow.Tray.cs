using System.Windows;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class MainWindow
{
    private TrayIconService? trayIcon;
    private bool trayRegistrationFailed;

    private void InitializeTray()
    {
        if (closing || trayIcon is not null) return;
        trayIcon = new TrayIconService(this, ActivateFromTray, TrayRegistrationChanged);
    }

    private void TrayRegistrationChanged(bool registered)
    {
        if (closing) return;
        trayRegistrationFailed = !registered;
        ShowInTaskbar = !registered;
        if (!registered)
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            SetStatus("트레이 아이콘 등록 실패 · 작업 표시줄에서 열 수 있습니다", false);
        }
        else if (ready)
        {
            SetStatus(connected ? "Codex 연결됨" : "Codex 연결 확인 중…", connected);
        }
    }

    private void ActivateFromTray()
    {
        if (closing) return;
        Window target = resetDialog is { IsVisible: true } dialog ? dialog : this;
        if (!target.IsVisible) target.Show();
        if (target.WindowState == WindowState.Minimized) target.WindowState = WindowState.Normal;
        target.Activate();
    }

    private void DisposeTray()
    {
        trayIcon?.Dispose();
        trayIcon = null;
    }

    private void Window_Closed(object? sender, EventArgs e) => DisposeTray();
}
