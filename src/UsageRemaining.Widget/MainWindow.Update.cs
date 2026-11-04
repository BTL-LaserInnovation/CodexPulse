using System.Windows;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class MainWindow
{
    private readonly UpdateCoordinator updates;
    private bool updateApplying;
    private string? updateRetry;

    private void Updates_Changed(object? sender, EventArgs e)
    {
        if (closing || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => Updates_Changed(sender, e)));
            return;
        }
        UpdateButton.Visibility = updates.AvailableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.IsEnabled = !updates.IsBusy && !updateApplying;
        UpdateButton.Content = updates.IsBusy ? $"{updates.Progress}%" : updates.IsDownloaded ? "Restart" :
            updates.Error is not null || updateRetry is not null ? "Retry" : "Update";
        UpdateButton.ToolTip = updates.Error ?? updateRetry ?? (updates.AvailableUpdate is { } update
            ? $"Codex Pulse {update.Version} 업데이트\n클릭하면 다운로드 후 설정을 저장하고 다시 시작합니다."
            : "새 업데이트가 없습니다.");
    }

    private void ShowUpdateRetry(string message)
    {
        updateRetry = message;
        Updates_Changed(this, EventArgs.Empty);
    }

    private void RestoreAfterUpdateFailure(string message)
    {
        closing = false;
        updateApplying = false;
        FooterRefreshButton.IsEnabled = !refreshing && !creditAction;
        RenderAccount();
        RenderCredits();
        ShowUpdateRetry(message);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (closing || updateApplying || updates.IsBusy || updates.AvailableUpdate is null) return;
        updateRetry = null;
        await updates.DownloadAsync(lifetime.Token);
        if (closing || !updates.IsDownloaded) return;
        if (creditAction || resetDialog is not null || refreshing)
        {
            ShowUpdateRetry("다운로드 완료 · 진행 중인 계정/리셋 작업이 끝나면 다시 눌러 재시작하세요.");
            return;
        }
        if (!SaveSettings(reportWarning: false))
        {
            ShowUpdateRetry("설정 저장 실패 · 설정을 저장할 수 있는지 확인한 후 다시 눌러 재시작하세요.");
            return;
        }
        updateApplying = true;
        FooterRefreshButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        RenderCredits();
        Updates_Changed(this, EventArgs.Empty);
        Close();
    }
}
