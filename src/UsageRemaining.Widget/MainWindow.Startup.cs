using System.Diagnostics;
using System.Windows;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class MainWindow
{
    private readonly IStartupRegistrationService startupRegistration;
    private bool startupApplied;
    private bool startupChanging;

    private void InitializeStartupControl()
    {
        AutoStartCheck.IsChecked = state.StartWithWindows;
        AutoStartCheck.IsEnabled = startupRegistration.IsAvailable;
        AutoStartCheck.ToolTip = startupRegistration.IsAvailable
            ? "Windows에 로그인하면 Codex Pulse를 자동으로 실행합니다."
            : startupRegistration.UnavailableReason;
    }

    private void ApplyStartupPreference()
    {
        if (startupApplied || !startupRegistration.IsAvailable) return;
        startupApplied = true;
        try
        {
            startupRegistration.Apply(state.StartWithWindows);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StartupRegistrationFailed(ex, unknownRegistration: true);
        }
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || startupChanging || closing || updateApplying || !startupRegistration.IsAvailable) return;
        var previous = state.StartWithWindows;
        var requested = AutoStartCheck.IsChecked == true;
        if (previous == requested) return;
        startupChanging = true;
        try
        {
            startupRegistration.Apply(requested);
            state.StartWithWindows = requested;
            if (!SaveSettings(reportWarning: false)) throw new InvalidOperationException(store.Warning);
            AutoStartCheck.ToolTip = "Windows에 로그인하면 Codex Pulse를 자동으로 실행합니다.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            state.StartWithWindows = previous;
            try
            {
                startupRegistration.Apply(previous);
                AutoStartCheck.IsChecked = previous;
                StartupRegistrationFailed(ex, unknownRegistration: false);
            }
            catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
            {
                StartupRegistrationFailed(new AggregateException(ex, rollbackError), unknownRegistration: true);
            }
        }
        finally
        {
            startupChanging = false;
        }
    }

    private void StartupRegistrationFailed(Exception error, bool unknownRegistration)
    {
        Trace.TraceWarning("Startup registration failed: {0}", error);
        if (unknownRegistration)
        {
            startupChanging = true;
            AutoStartCheck.IsChecked = null;
            AutoStartCheck.IsEnabled = false;
            startupChanging = false;
        }
        var message = unknownRegistration
            ? "자동 실행 상태를 확인할 수 없습니다. 앱을 다시 열어 확인하세요."
            : "자동 실행 설정을 변경하지 못했습니다. 이전 설정을 유지합니다.";
        AutoStartCheck.ToolTip = message + "\n" + error.Message;
        SetStatus(message, false, error.Message);
    }
}
