using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class MainWindow
{
    private double desiredCompactHeight = 84;

    private void RenderCompactUsage(bool stale)
    {
        var windows = snapshot?.Windows.Take(2).OrderBy(window => window.DurationMinutes == 10080 ? 0 : 1).ToArray()
            ?? [];
        RenderCompactWindow(windows.ElementAtOrDefault(0), CompactName, CompactPercent, CompactReset, CompactProgress, "MintBrush", stale);
        var secondary = windows.ElementAtOrDefault(1);
        if (secondary is not null)
            RenderCompactWindow(secondary, CompactSecondaryName, CompactSecondaryPercent, CompactSecondaryReset, CompactSecondaryProgress, "VioletBrush", stale);
        else
        {
            CompactSecondaryName.Text = "";
            CompactSecondaryPercent.Text = "";
            CompactSecondaryReset.Text = "";
            CompactSecondaryPercent.ToolTip = null;
            CompactSecondaryReset.ToolTip = null;
            CompactSecondaryProgress.Value = 0;
            AutomationProperties.SetName(CompactSecondaryPercent, "");
            AutomationProperties.SetName(CompactSecondaryProgress, "");
        }
        var secondaryVisibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        CompactSecondaryName.Visibility = secondaryVisibility;
        CompactSecondaryPercent.Visibility = secondaryVisibility;
        CompactSecondaryReset.Visibility = secondaryVisibility;
        CompactSecondaryProgress.Visibility = secondaryVisibility;
        CompactSecondaryRow.Height = new GridLength(secondary is null ? 0 : 28);
        CompactPanel.Height = secondary is null ? 28 : 56;
        desiredCompactHeight = secondary is null ? 84 : 112;
        CompactPanel.ToolTip = $"{(connected ? "연동 계정" : "이전 연결 계정")}: {accountEmail ?? "확인 중"}\n{(windows.Length == 0 ? "제공된 사용 한도 정보가 없습니다." : "서버가 제공한 사용 한도입니다.")}{(stale ? "\n연결 확인 전의 이전 값입니다." : "")}";
        if (state.IsCollapsed && Height != desiredCompactHeight)
        {
            Height = desiredCompactHeight;
            ClampToWorkArea();
        }
    }

    private void RenderCompactWindow(RateWindow? window, TextBlock name, TextBlock percent, TextBlock reset, ProgressBar progress, string accent, bool stale)
    {
        name.Text = window?.CompactLabel ?? "사용 한도";
        name.Foreground = Theme(window is null ? "MutedBrush" : accent);
        percent.Text = window is null ? "—%" : $"{window.RemainingPercent:0.#}%";
        var color = window is null ? "MutedBrush" : window.RemainingPercent <= 15 ? "CoralBrush" : accent;
        percent.Foreground = Theme(color);
        progress.Value = window?.RemainingPercent ?? 0;
        progress.Foreground = Theme(color);
        reset.Text = window is null ? "한도 정보 없음" : window.ResetsAt is { } at ? Countdown(at) : "리셋 시각 미제공";
        reset.ToolTip = (window?.ResetsAt is { } time
            ? $"다음 리셋: {LocalTime.Convert(time):yyyy-MM-dd HH:mm:ss} {LocalTime.Label(time)}\nPC 시간대: {TimeZoneInfo.Local.DisplayName}\n서버가 제공한 다음 일정만 표시합니다."
            : reset.Text) + (stale ? "\n연결 확인 전의 이전 값입니다." : "");
        percent.ToolTip = $"{name.Text} 남은 사용량 비율";
        AutomationProperties.SetName(percent, $"{name.Text} 남은 사용량");
        AutomationProperties.SetName(progress, $"{name.Text} 남은 사용량 비율");
    }
}
