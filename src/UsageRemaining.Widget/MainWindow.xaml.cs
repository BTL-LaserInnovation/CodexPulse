using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class MainWindow : Window
{
    private readonly LocalStateStore store = new();
    private readonly WidgetState state;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private CodexAppServerClient? client;
    private UsageSnapshot? snapshot;
    private DateTime month = new(LocalTime.Convert(DateTimeOffset.UtcNow).Year, LocalTime.Convert(DateTimeOffset.UtcNow).Month, 1);
    private DateTime selectedDate = LocalTime.Convert(DateTimeOffset.UtcNow).Date;
    private DateTime visibleWeek = LocalTime.Convert(DateTimeOffset.UtcNow).Date;
    private DateTime displayedToday = LocalTime.Convert(DateTimeOffset.UtcNow).Date;
    private TimeZoneInfo displayZone = TimeZoneInfo.Local;
    private TimeSpan displayOffset = LocalTime.Convert(DateTimeOffset.UtcNow).Offset;
    private long nextZoneCheck;
    private DateTimeOffset nextRefresh = DateTimeOffset.MinValue;
    private bool ready;
    private bool refreshing;
    private bool refreshQueued;
    private bool closing;
    private bool closeComplete;
    private bool connected;
    private bool needsLogin;
    private long accountGeneration;
    private string? accountEmail;
    private string? accountPlan;
    private string? accountFingerprint;
    private ResetCreditBalance creditBalance = new(null, [], false);
    private bool creditAction;
    private ResetConfirmDialog? resetDialog;
    private readonly PublicResetCalendarClient publicClient = new();
    private bool publicRefreshing;
    private long nextPublicRefresh;
    private string publicStatus = "공개 이력 연결 중…";
    private long nextCreditRender;

    public MainWindow() : this(new AppUpdateService())
    {
    }

    public MainWindow(IAppUpdateService updateService) : this(updateService, new StartupRegistrationService())
    {
    }

    public MainWindow(IAppUpdateService updateService, IStartupRegistrationService startupService)
        : this(updateService, startupService, new GoogleCalendarService())
    {
    }

    public MainWindow(IAppUpdateService updateService, IStartupRegistrationService startupService, IGoogleCalendarService googleService)
    {
        ArgumentNullException.ThrowIfNull(startupService);
        ArgumentNullException.ThrowIfNull(googleService);
        startupRegistration = startupService;
        googleCalendar = googleService;
        updates = new(updateService);
        state = store.Load();
        InitializeComponent();
        ApplyCollapsedState();
        Topmost = state.AlwaysOnTop;
        TopmostCheck.IsChecked = Topmost;
        InitializeStartupControl();
        BackgroundOpacity.SetValue(this, state.Opacity);
        state.Opacity = BackgroundOpacity.GetValue(this);
        OpacitySlider.Value = state.Opacity;
        UpdateOpacityToolTip();
        if (state.Left is { } left && state.Top is { } top && double.IsFinite(left) && double.IsFinite(top) &&
            left >= SystemParameters.VirtualScreenLeft && top >= SystemParameters.VirtualScreenTop &&
            left + Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top + Height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        snapshot = History?.LastSnapshot;
        accountEmail = History?.Email;
        accountPlan = History?.Plan;
        if (state.PublicCalendar is not null) publicStatus = "공개 이력 · 저장 자료";
        ready = true;
        SetStatus("Codex 연결 중…", false);
        RenderUsage();
        RenderAccount();
        RenderCredits();
        RenderCalendar();
        Loaded += Window_Loaded;
        Closing += Window_Closing;
        Closed += Window_Closed;
        clock.Tick += Clock_Tick;
        updates.Changed += Updates_Changed;
        googleCalendar.Changed += GoogleCalendar_Changed;
        RenderGoogleConnection();
    }

    private AccountHistory? History => state.ActiveAccount is { } key && state.Accounts.TryGetValue(key, out var history) ? history : null;
    private Brush Theme(string key) => (Brush)FindResource(key);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyStartupPreference();
        InitializeTray();
        clock.Start();
        ClampToWorkArea();
        (Application.Current as App)?.MainWindowLoaded(this);
        _ = updates.CheckIfDueAsync(lifetime.Token);
        _ = RefreshPublicCalendarAsync();
        _ = InitializeGoogleAsync();
        await RefreshAsync();
    }

    private async void Clock_Tick(object? sender, EventArgs e)
    {
        if (closing) return;
        _ = updates.CheckIfDueAsync(lifetime.Token);
        if (googleInitialized && Environment.TickCount64 >= nextGoogleRefresh && !googleRefreshing)
            _ = RefreshGoogleAsync();
        RefreshLocalTime();
        RenderUsage();
        if (Environment.TickCount64 >= nextCreditRender)
        {
            RenderCredits();
            nextCreditRender = Environment.TickCount64 + 10_000;
        }
        if (Environment.TickCount64 >= nextPublicRefresh && !publicRefreshing)
            _ = RefreshPublicCalendarAsync();
        if (DateTimeOffset.UtcNow >= nextRefresh && !refreshing)
            await RefreshAsync();
    }

    private void RefreshLocalTime()
    {
        var now = DateTimeOffset.UtcNow;
        var zoneChanged = false;
        var ticks = Environment.TickCount64;
        if (ticks >= nextZoneCheck)
        {
            // TimeZoneInfo.Local is cached. Re-read Windows settings without changing them.
            TimeZoneInfo.ClearCachedData();
            var currentZone = TimeZoneInfo.Local;
            zoneChanged = displayZone.Id != currentZone.Id || !displayZone.HasSameRules(currentZone);
            displayZone = currentZone;
            nextZoneCheck = ticks + 10_000;
        }
        var local = LocalTime.Convert(now);
        if (!zoneChanged && local.Date == displayedToday && local.Offset == displayOffset) return;
        UpdateCalendarToday(local.Date);
        displayOffset = local.Offset;
        RenderCalendar();
        RenderCredits();
        _ = RefreshGoogleAsync(force: true);
    }

    private void UpdateCalendarToday(DateTime localToday)
    {
        var followingToday = selectedDate == displayedToday && (state.IsCalendarCollapsed
            ? VisibleCalendarRange.Contains(displayedToday)
            : month.Year == displayedToday.Year && month.Month == displayedToday.Month);
        if (followingToday)
        {
            selectedDate = localToday.Date;
            month = new(selectedDate.Year, selectedDate.Month, 1);
            visibleWeek = selectedDate;
        }
        displayedToday = localToday.Date;
    }

    private void EnsureClient()
    {
        if (client is not null) return;
        var executable = CodexExecutableLocator.IsValidExecutable(state.CodexExecutable)
            ? state.CodexExecutable : CodexExecutableLocator.FindExecutable();
        if (executable is null) throw new InvalidOperationException("Codex 실행 파일을 찾지 못했습니다. ‘연결’에서 선택하세요.");
        state.CodexExecutable = executable;
        client = new(executable);
        client.RateLimitsChanged += ProviderChanged;
        client.AccountChanged += AccountChanged;
        client.LoginCompleted += ProviderChanged;
    }

    private void ProviderChanged(object? sender, EventArgs e)
    {
        if (!closing && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(async () => await RefreshAsync()));
    }

    private void AccountChanged(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref accountGeneration);
        if (!Dispatcher.HasShutdownStarted)
            _ = Dispatcher.BeginInvoke(new Action(() => resetDialog?.Invalidate("계정이 변경되었습니다. 취소 후 다시 선택하세요.")));
        ProviderChanged(sender, e);
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (closing || updateApplying) return;
        if (creditAction && !force) { refreshQueued = true; return; }
        if (refreshing) { refreshQueued = true; return; }
        refreshing = true;
        FooterRefreshButton.IsEnabled = false;
        try
        {
            SetStatus("사용량 확인 중…", false);
            EnsureClient();
            var currentClient = client!;
            var generation = Interlocked.Read(ref accountGeneration);
            var protocolGeneration = currentClient.AccountRevision;
            var accountResult = await currentClient.ReadAccountAsync(lifetime.Token);
            if (!accountResult.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object ||
                !account.TryGetProperty("type", out var accountType) || accountType.GetString() != "chatgpt")
            {
                needsLogin = true;
                connected = false;
                state.ActiveAccount = null;
                snapshot = null;
                accountEmail = null;
                accountPlan = null;
                accountFingerprint = null;
                creditBalance = new(null, [], false);
                SetStatus("ChatGPT 계정 로그인이 필요합니다", false);
                ConnectButton.Content = "로그인";
                return;
            }
            needsLogin = false;
            var fingerprint = AccountKey(accountResult, account);
            var result = await currentClient.ReadRateLimitsAsync(lifetime.Token);
            var verifiedAccount = await currentClient.ReadAccountAsync(lifetime.Token);
            if (generation != Interlocked.Read(ref accountGeneration) || protocolGeneration != currentClient.AccountRevision ||
                !verifiedAccount.TryGetProperty("account", out var verified) || verified.ValueKind != JsonValueKind.Object ||
                AccountKey(verifiedAccount, verified) != fingerprint)
            {
                connected = false;
                snapshot = null;
                creditBalance = new(null, [], false);
                refreshQueued = true;
                SetStatus("계정 변경 확인 중…", false);
                return;
            }
            var identity = result.TryGetProperty("accountId", out var quotaAccount) && quotaAccount.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(quotaAccount.GetString())
                ? HashIdentity(fingerprint + ":" + quotaAccount.GetString()) : fingerprint;
            // Verify identity after fetching usage before committing any snapshot or history.
            if (state.ActiveAccount != identity)
            {
                state.ActiveAccount = identity;
                snapshot = null;
            }
            if (!state.Accounts.TryGetValue(identity, out var history))
                state.Accounts[identity] = history = new();
            snapshot = history.LastSnapshot;
            accountEmail = account.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String ? email.GetString() : null;
            accountPlan = account.TryGetProperty("planType", out var plan) && plan.ValueKind == JsonValueKind.String ? plan.GetString() : null;
            accountFingerprint = fingerprint;
            history.Email = accountEmail;
            history.Plan = accountPlan;
            creditBalance = ResetCreditBalance.Parse(result);
            var latest = UsageParser.Parse(result, DateTimeOffset.UtcNow);
            history.LastSnapshot = latest;
            snapshot = latest;
            connected = true;
            ConnectButton.Content = "연결됨";
            SetStatus("Codex 연결됨", true);
            if (!store.Save(state)) SetStatus(store.Warning!, false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            connected = false;
            SetStatus("연결 실패 · 마지막 값 유지", false, ex.Message);
            ConnectButton.Content = needsLogin ? "로그인" : "재연결";
            await DropClientAsync();
        }
        finally
        {
            nextRefresh = DateTimeOffset.UtcNow.AddSeconds(60);
            refreshing = false;
            if (!closing)
            {
                FooterRefreshButton.IsEnabled = !creditAction && !updateApplying;
                RenderUsage();
                RenderAccount();
                RenderCredits();
                RenderCalendar();
                if (refreshQueued && !creditAction)
                {
                    refreshQueued = false;
                    _ = Dispatcher.BeginInvoke(new Action(async () => await RefreshAsync()));
                }
            }
        }
    }

    private static string AccountKey(JsonElement result, JsonElement account)
    {
        var accountId = result.TryGetProperty("accountId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        var email = account.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        // Account metadata is only used to partition history; credentials are never read or persisted.
        var nestedId = account.TryGetProperty("id", out var nested) && nested.ValueKind == JsonValueKind.String ? nested.GetString() : null;
        var source = accountId ?? nestedId ?? email;
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("계정을 식별할 수 없어 사용량 기록을 분리하지 못했습니다.");
        return HashIdentity(source);
    }

    private static string HashIdentity(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    private void SetStatus(string message, bool success, string? detail = null)
    {
        if (trayRegistrationFailed)
        {
            detail = "트레이 아이콘 등록 실패 · 작업 표시줄에서 열 수 있습니다\n" + (detail ?? message);
            message = "트레이 아이콘 등록 실패 · 작업 표시줄 사용";
            success = false;
        }
        StatusText.Text = message;
        StatusText.ToolTip = detail ?? message;
        StatusDot.Fill = Theme(success ? "MintBrush" : "CoralBrush");
    }

    private void RenderUsage()
    {
        if (!ready) return;
        var stale = !connected || snapshot is null || DateTimeOffset.UtcNow - snapshot.CapturedAt > TimeSpan.FromMinutes(2);
        var windows = DisplayedQuotaWindows();
        RenderWindow(windows.ElementAtOrDefault(0), PrimaryName, PrimaryPercent, PrimaryReset, PrimaryProgress, QuotaAccent(windows.ElementAtOrDefault(0)));
        RenderWindow(windows.ElementAtOrDefault(1), SecondaryName, SecondaryPercent, SecondaryReset, SecondaryProgress, QuotaAccent(windows.ElementAtOrDefault(1)));
        RenderCompactUsage(stale);
        UpdatedText.Text = snapshot is null ? $"60초마다 갱신 · {LocalTime.Label(DateTimeOffset.UtcNow)}" :
            $"{(stale ? "이전 값 · " : "")}{LocalTime.Convert(snapshot.CapturedAt):MM/dd HH:mm:ss} 갱신 · {LocalTime.Label(snapshot.CapturedAt)}";
        UpdatedText.ToolTip = snapshot is null ? $"PC 시간대: {TimeZoneInfo.Local.DisplayName}\n조회된 수치가 없으면 —로 표시합니다." :
            $"마지막 조회: {LocalTime.Convert(snapshot.CapturedAt):yyyy-MM-dd HH:mm:ss} {LocalTime.Label(snapshot.CapturedAt)}\nPC 시간대: {TimeZoneInfo.Local.DisplayName}\n60초마다 조회합니다. 연결이 끊겨도 비율을 임의로 변경하지 않습니다.";
    }

    private void RenderWindow(RateWindow? window, TextBlock name, TextBlock percent, TextBlock reset, ProgressBar progress, string accent)
    {
        name.Text = window?.Label ?? "한도 정보 없음";
        name.Foreground = Theme(accent);
        percent.Text = window is null ? "— %" : $"{window.RemainingPercent:0.#}%";
        progress.Value = window?.RemainingPercent ?? 0;
        var color = window is { RemainingPercent: <= 15 } ? "CoralBrush" : accent;
        percent.Foreground = Theme(color);
        progress.Foreground = Theme(color);
        if (window?.ResetsAt is not { } at)
            reset.Text = window is null ? "제공된 한도 없음" : "리셋 시각 미제공";
        else
            reset.Text = $"{LocalTime.Convert(at):MM-dd HH:mm} 리셋";
        reset.ToolTip = window?.ResetsAt is { } time ? $"다음 리셋: {LocalTime.Convert(time):yyyy-MM-dd HH:mm:ss} {LocalTime.Label(time)}\nPC 시간대: {TimeZoneInfo.Local.DisplayName}\n서버가 제공한 다음 일정만 표시합니다." : reset.Text;
        percent.ToolTip = "남은 사용량 비율";
    }


    private void CalendarDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DateTime date })
        {
            var previousRange = VisibleCalendarRange;
            selectedDate = date;
            selectedGoogleEvent = null;
            if (!state.IsCalendarCollapsed) month = new(date.Year, date.Month, 1);
            RenderCalendar();
            if (previousRange != VisibleCalendarRange) _ = RefreshGoogleAsync(force: true);
        }
    }
    private void PreviousMonth_Click(object sender, RoutedEventArgs e) => NavigateCalendar(-1);
    private void NextMonth_Click(object sender, RoutedEventArgs e) => NavigateCalendar(1);

    private void NavigateCalendar(int direction)
    {
        if (state.IsCalendarCollapsed)
        {
            var candidate = visibleWeek.AddDays(direction * 7);
            if (candidate.Year < 2 || candidate.Year > 9998) return;
            visibleWeek = candidate;
        }
        else
        {
            if (direction < 0 && month.Year <= 2 || direction > 0 && month.Year >= 9998) return;
            month = month.AddMonths(direction);
        }
        RenderCalendar();
        _ = RefreshGoogleAsync(force: true);
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        selectedDate = LocalTime.Convert(DateTimeOffset.UtcNow).Date;
        month = new(selectedDate.Year, selectedDate.Month, 1);
        visibleWeek = selectedDate;
        selectedGoogleEvent = null;
        RenderCalendar();
        _ = RefreshGoogleAsync(force: true);
    }

    private void ToggleCalendarCollapse_Click(object sender, RoutedEventArgs e)
    {
        state.IsCalendarCollapsed = !state.IsCalendarCollapsed;
        if (state.IsCalendarCollapsed)
        {
            selectedDate = LocalTime.Convert(DateTimeOffset.UtcNow).Date;
            visibleWeek = selectedDate;
            selectedGoogleEvent = null;
        }
        month = new(selectedDate.Year, selectedDate.Month, 1);
        ApplyCollapsedState();
        RenderCalendar();
        ClampToWorkArea();
        SaveSettings();
        _ = RefreshGoogleAsync(force: true);
    }
    private void Topmost_Changed(object sender, RoutedEventArgs e) { if (ready) { Topmost = TopmostCheck.IsChecked == true; state.AlwaysOnTop = Topmost; SaveSettings(); } }
    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        BackgroundOpacity.SetValue(this, OpacitySlider.Value);
        state.Opacity = BackgroundOpacity.GetValue(this);
        UpdateOpacityToolTip();
    }
    private void UpdateOpacityToolTip() => OpacitySlider.ToolTip = $"배경 불투명도 {state.Opacity:P0} (투명도 {1 - state.Opacity:P0})";
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshPublicCalendarAsync();
        _ = RefreshGoogleAsync(force: true);
        await RefreshAsync();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (refreshing || closing || creditAction || updateApplying) return;
        ConnectButton.IsEnabled = false;
        try
        {
            if (!CodexExecutableLocator.IsValidExecutable(state.CodexExecutable) && CodexExecutableLocator.FindExecutable() is null)
            {
                var dialog = new OpenFileDialog { Title = "Codex 실행 파일 선택", Filter = "Codex 실행 파일 (codex.exe)|codex.exe", CheckFileExists = true };
                if (dialog.ShowDialog(this) != true) return;
                state.CodexExecutable = dialog.FileName;
            }
            if (needsLogin)
            {
                EnsureClient();
                var url = await client!.StartLoginAsync(lifetime.Token);
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                    (uri.Host == "chatgpt.com" || uri.Host == "auth.openai.com"))
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    SetStatus("브라우저에서 로그인을 완료하세요", false);
                }
                else throw new InvalidOperationException("안전한 로그인 주소를 받지 못했습니다.");
            }
            else await RefreshAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetStatus("연결을 완료하지 못했습니다", false, ex.Message); await DropClientAsync(); }
        finally { if (!closing) ConnectButton.IsEnabled = true; }
    }

    private bool SaveSettings(bool reportWarning = true)
    {
        state.Left = Left;
        state.Top = Top;
        if (store.Save(state)) return true;
        if (reportWarning) SetStatus(store.Warning!, false);
        return false;
    }

    private async Task DropClientAsync()
    {
        var previous = client;
        client = null;
        if (previous is null) return;
        previous.RateLimitsChanged -= ProviderChanged;
        previous.AccountChanged -= AccountChanged;
        previous.LoginCompleted -= ProviderChanged;
        await previous.DisposeAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeComplete) return;
        e.Cancel = true;
        if (closing) return;
        if (updateApplying && (creditAction || resetDialog is not null || refreshing || googleConnectBusy || googleDialog is not null || googleDeleting || !SaveSettings(reportWarning: false)))
        {
            RestoreAfterUpdateFailure("업데이트를 적용하지 못했습니다. 진행 중인 작업과 설정 저장 상태를 확인한 후 다시 누르세요.");
            return;
        }
        closing = true;
        if (updateApplying)
        {
            // Finish account-client cleanup before asking the updater to wait for graceful process exit.
            try
            {
                await DropClientAsync();
                if (!SaveSettings(reportWarning: false))
                {
                    RestoreAfterUpdateFailure("설정 저장 실패 · 설정을 저장할 수 있는지 확인한 후 다시 눌러 재시작하세요.");
                    _ = RefreshAsync();
                    return;
                }
                updates.PrepareRestart();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RestoreAfterUpdateFailure("업데이트 재시작 준비 실패: " + ex.Message);
                _ = RefreshAsync();
                return;
            }
        }
        DisposeTray();
        clock.Stop();
        lifetime.Cancel();
        resetDialog?.Close();
        googleDialog?.Close();
        await googleConnectTask;
        googleCalendar.Changed -= GoogleCalendar_Changed;
        googleCalendar.Dispose();
        publicClient.Dispose();
        if (!updateApplying) SaveSettings();
        updates.Changed -= Updates_Changed;
        await updates.StopAsync();
        await DropClientAsync();
        lifetime.Dispose();
        closeComplete = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
