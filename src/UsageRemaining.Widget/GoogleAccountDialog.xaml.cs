using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class GoogleAccountDialog : Window
{
    private readonly IGoogleCalendarService service;
    private readonly Action<Uri> openBrowser;
    private readonly Func<CancellationToken, Task>? recheckConfigurationCallback;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private bool dirty;
    private bool closed;
    private bool closeRequested;
    private string? shownAccount;
    private string? initialError;

    public GoogleAccountDialog(IGoogleCalendarService service, Action<Uri> openBrowser,
        Func<CancellationToken, Task>? recheckConfigurationCallback = null, string? initialError = null)
    {
        this.service = service;
        this.openBrowser = openBrowser;
        this.recheckConfigurationCallback = recheckConfigurationCallback;
        InitializeComponent();
        BackgroundOpacity.SetValue(this, 1);
        RenderState(true);
        if (!string.IsNullOrWhiteSpace(initialError))
        {
            if (!service.State.Connected) this.initialError = initialError;
            StatusText.Text = initialError;
        }
        service.Changed += Service_Changed;
        Loaded += (_, _) =>
        {
            if (!service.State.Connected && ConnectButton.IsVisible && ConnectButton.IsEnabled) ConnectButton.Focus();
            else CancelButton.Focus();
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            closed = true;
            service.Changed -= Service_Changed;
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    private void Service_Changed(object? sender, EventArgs e)
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) RenderState(false);
        else Dispatcher.BeginInvoke(() => { if (!closed) RenderState(false); });
    }

    private void RenderState(bool reloadSelection)
    {
        var state = service.State;
        if (state.Connected && initialError is not null)
        {
            initialError = null;
            StatusText.Text = string.Empty;
        }
        AccountText.Text = state.Connected ? state.AccountEmail ?? "Google 계정 연결됨" : "연결된 계정 없음";
        AccountText.ToolTip = AccountText.Text;
        AccountHintText.Text = state.Connected ? "계정 변경은 브라우저에서 새 계정을 선택합니다."
            : state.IsConfigured ? "브라우저에서 Google 계정을 선택하고 캘린더 접근을 허용하세요."
            : "Google 연결 준비가 아직 완료되지 않았습니다. 추가 설치는 필요하지 않습니다.";
        var canRecheck = recheckConfigurationCallback is not null;
        ConnectionActions.Visibility = state.IsConfigured || canRecheck ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.Content = state.Connected ? "계정 변경" : state.IsConfigured ? "Google로 로그인" : "연결 다시 확인";
        ConnectButton.IsEnabled = !busy && (state.IsConfigured || canRecheck);
        ConnectButton.IsDefault = !state.Connected && (state.IsConfigured || canRecheck);
        Grid.SetColumnSpan(ConnectButton, state.Connected ? 1 : 3);
        DisconnectButton.Visibility = state.Connected ? Visibility.Visible : Visibility.Collapsed;
        DisconnectButton.IsEnabled = !busy && state.Connected;
        SaveButton.Visibility = state.Connected ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !busy && state.Connected;
        SaveButton.IsDefault = state.Connected;
        CancelButton.IsDefault = !state.Connected && !state.IsConfigured && !canRecheck;
        CalendarPanel.Visibility = state.Connected ? Visibility.Visible : Visibility.Collapsed;
        ConfigurationText.Visibility = state.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        ConfigurationText.Text = "앱 개발자의 Google 연결 등록이 완료되면 브라우저에서 로그인할 수 있습니다.";
        SecurityText.Visibility = state.IsConfigured || state.Connected ? Visibility.Visible : Visibility.Collapsed;
        if (reloadSelection || shownAccount != state.AccountEmail || !dirty && !busy)
        {
            shownAccount = state.AccountEmail;
            CalendarList.Children.Clear();
            foreach (var calendar in state.Calendars)
            {
                var check = new CheckBox
                {
                    Content = new TextBlock
                    {
                        Text = calendar.Summary + (calendar.CanWrite ? "" : " · 읽기 전용"),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 244,
                        FontSize = 10
                    },
                    Tag = calendar.Id,
                    IsChecked = state.SelectedCalendarIds.Contains(calendar.Id),
                    Margin = new Thickness(0, 7, 0, 7),
                    ToolTip = calendar.Summary + "\n" + calendar.Id
                };
                check.Checked += Selection_Changed;
                check.Unchecked += Selection_Changed;
                CalendarList.Children.Add(check);
            }
            dirty = false;
        }
        EmptyCalendarsText.Visibility = state.Calendars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!busy && state.IsConfigured && string.IsNullOrWhiteSpace(StatusText.Text) && !string.IsNullOrWhiteSpace(state.Warning))
            StatusText.Text = state.Warning;
    }

    private void Selection_Changed(object sender, RoutedEventArgs e) => dirty = true;

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !service.State.IsConfigured && recheckConfigurationCallback is null) return;
        if (service.State.Connected)
        {
            var confirm = new GoogleActionConfirmDialog(this, "Google 계정을 변경할까요?", "브라우저에서 새 계정을 선택합니다. 연결에 성공하면 캘린더 목록을 새로 불러옵니다.", "계정 변경");
            if (confirm.ShowDialog() != true) return;
        }
        await RunAsync(async token =>
        {
            if (!service.State.IsConfigured && recheckConfigurationCallback is not null)
            {
                await recheckConfigurationCallback(token);
                token.ThrowIfCancellationRequested();
                RenderState(false);
                if (!service.State.IsConfigured)
                {
                    StatusText.Text = "Google 연결 준비를 다시 확인했습니다. 앱 개발자의 연결 등록을 기다려 주세요.";
                    return;
                }
                StatusText.Text = "브라우저에서 Google 계정 연결을 완료하세요…";
            }
            await service.ConnectAsync(openBrowser, token);
            RenderState(true);
            StatusText.Text = "Google 계정이 연결되었습니다. 표시할 캘린더를 선택하세요.";
        }, service.State.IsConfigured ? "브라우저에서 Google 계정 연결을 완료하세요…" : "Google 연결 준비를 확인하는 중…");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !service.State.Connected) return;
        var confirm = new GoogleActionConfirmDialog(this, "Google 연결을 해제할까요?", "이 컴퓨터에 저장된 Google 연결 정보와 캘린더 캐시를 지웁니다. Google의 원본 일정은 유지됩니다.", "연결 해제");
        if (confirm.ShowDialog() != true) return;
        await RunAsync(async token =>
        {
            await service.DisconnectAsync(token);
            RenderState(true);
            StatusText.Text = "Google 계정 연결을 해제했습니다.";
        }, "연결 해제 중…");
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !service.State.Connected) return;
        var selected = CalendarList.Children.OfType<CheckBox>().Where(item => item.IsChecked == true).Select(item => (string)item.Tag).ToArray();
        await RunAsync(async token =>
        {
            await service.SelectCalendarsAsync(selected, token);
            dirty = false;
            RenderState(true);
            StatusText.Text = "표시할 캘린더를 저장했습니다.";
        }, "캘린더 선택 저장 중…");
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, string message)
    {
        initialError = null;
        busy = true;
        CalendarList.IsEnabled = false;
        StatusText.Text = message;
        CancelButton.Content = "중단";
        RenderState(false);
        try { await operation(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var errorMessage = string.IsNullOrWhiteSpace(exception.Message)
                ? "Google 계정 작업을 완료하지 못했습니다. 잠시 후 다시 시도해 주세요."
                : exception.Message;
            StatusText.Text = service.State.IsConfigured ? errorMessage : "Google 연결 준비를 확인하지 못했습니다. 잠시 후 다시 확인해 주세요.";
        }
        finally
        {
            busy = false;
            CalendarList.IsEnabled = true;
            CancelButton.Content = "닫기";
            RenderState(false);
            if (closeRequested) Close();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        e.Cancel = true;
        closeRequested = true;
        lifetime.Cancel();
        StatusText.Text = "작업 중단을 기다리는 중…";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { closeRequested = true; lifetime.Cancel(); StatusText.Text = "작업 중단을 기다리는 중…"; }
        else Close();
    }

    private void Dialog_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && WindowInteraction.CanDragFrom(e.OriginalSource as DependencyObject)) DragMove();
    }
}
