using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static int GoogleUiChecks(bool agendaOnly = false)
    {
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "google-ui", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", folder);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = XDocument.Load(Path.Combine("src", "UsageRemaining.Widget", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = source.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "services", "clr-namespace:UsageRemaining.Widget.Services;assembly=CodexPulse"),
            resources.Elements().Select(element => new XElement(element)));
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var exitCode = 0;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            MainWindow? window = null;
            try
            {
                var google = new FakeGoogleCalendarService();
                window = new MainWindow(new UpdateChecks.FakeUpdateService(), new StartupChecks.FakeStartupService(), google);
                DetachStartupRuntimeLoading(window);
                window.Show();
                window.UpdateLayout();
                Check(window.Width == 360 && window.Height == 572, "Google integration retains 360 DIP width and provides expanded calendar detail height");
                Check(((FrameworkElement)window.FindName("GoogleCalendarOverlay")).ActualWidth > 0,
                    "Google calendar overlay is measured with the month grid");
                SetWindowField(window, "selectedDate", new DateTime(2026, 11, 9));
                SetWindowField(window, "month", new DateTime(2026, 11, 1));
                SeedGoogleCalendarGeometry(window);
                InvokeStartupRender(window, "RenderCalendar");
                window.UpdateLayout();
                var list = (StackPanel)window.FindName("CalendarEventList");
                Check(list.Children.Count >= 4, "Google selected-day details retain every same-day event");
                var scroll = (ScrollViewer)window.FindName("CalendarDetailScroll");
                Check(scroll.ExtentHeight > scroll.ViewportHeight && Math.Abs(scroll.ActualHeight - 48) < .1,
                    "Google details show two rows and scroll for additional entries");
                var canvas = (Canvas)window.FindName("GoogleCalendarOverlay");
                Check(canvas.Children.Count >= 2 && canvas.Children.OfType<FrameworkElement>().All(child => child.ActualWidth > 0),
                    "Google multiday events render visible overlay spans across week boundaries");
                Capture(window, "google-calendar-fixture");
                CheckGoogleCalendarGeometry(window);
                CheckGoogleCreditFontParity(window);
                if (agendaOnly)
                {
                    CheckCompactAgendaLayout(window, google);
                    await CheckAgendaSyncFeedback(window, google);
                    Console.WriteLine($"All {passed} agenda checks passed. Fake accounts and isolated settings only.");
                    return;
                }
                CheckGoogleHolidayPresentation(window, google);
                GoogleMainWindowCrudChecks(window, google);
                await GoogleDirectDeleteChecks(window, google);
                await GoogleOpaqueDialogChecks(window, google);
                await GoogleEditorChecks(window, google.State.Calendars);
                await GoogleDatePickerChecks(window, google.State.Calendars);
                google.SelectionFailure = new IOException("Fixture calendar selection failure");
                var account = new GoogleAccountDialog(google, _ => throw new InvalidOperationException("Fixture must not open a browser")) { Owner = window };
                account.Show();
                var selections = ((StackPanel)account.FindName("CalendarList")).Children.OfType<CheckBox>().ToArray();
                selections.Single(item => (string)item.Tag == "readonly-calendar").IsChecked = true;
                ((Button)account.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => ((TextBlock)account.FindName("StatusText")).Text.Contains("Fixture calendar selection failure"), "Google account selection failure is displayed");
                Check(selections.All(item => item.IsChecked == true) && ((Button)account.FindName("SaveButton")).IsEnabled,
                    "Google account selection failure retains checkbox edits and enables retry");
                Capture(account, "google-account-fixture");
                account.Close();
                google.SelectionFailure = null;
                await GoogleConnectionEntryChecks(window, google);
                ((Button)window.FindName("CollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                CheckCompactTransitions(window);
                ((Button)window.FindName("CollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Capture(window, "google-quota-expanded-fixture");
                await GoogleDeleteCloseChecks(window, google);
                await WaitUntilAsync(() => WindowField<bool>(window, "closeComplete"), "Google fixture closes without outstanding operations");
                Check(google.Disposed, "Google service lifetime ends with its window");
                window = null;
                Console.WriteLine($"All {passed} Google UI checks passed. Only fake services and isolated state were used.");
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); exitCode = 1; }
            finally
            {
                foreach (var dialog in app.Windows.OfType<Window>().Where(value => value is GoogleEventDialog or GoogleAccountDialog).ToArray()) dialog.Close();
                if (window is not null && !WindowField<bool>(window, "closing")) window.Close();
                app.Shutdown(exitCode);
            }
        }));
        app.Run();
        return exitCode;
    }

    private static int GooglePreview(bool smoke)
    {
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "google-preview", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", folder);
        var app = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
        var source = XDocument.Load(Path.Combine("src", "UsageRemaining.Widget", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "services", "clr-namespace:UsageRemaining.Widget.Services;assembly=CodexPulse"),
            source.Root!.Element(presentation + "Application.Resources")!.Elements().Select(element => new XElement(element)));
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var google = new FakeGoogleCalendarService();
        var window = new MainWindow(new UpdateChecks.FakeUpdateService(), new StartupChecks.FakeStartupService(), google)
        {
            Title = "Codex Pulse · 예시"
        };
        DetachStartupRuntimeLoading(window);
        ((TextBlock)window.FindName("AppTitle")).Text = "Codex Pulse · 예시";
        SetWindowField(window, "connected", true);
        SetWindowField(window, "accountEmail", "preview@example.invalid");
        SetWindowField(window, "accountPlan", "plus");
        var now = DateTimeOffset.UtcNow;
        SetWindowField(window, "snapshot", new UsageSnapshot(now,
            [new("primary", 12, 300, now.AddHours(4)), new("secondary", 37, 10080, now.AddDays(4))]));
        SetWindowField(window, "selectedDate", new DateTime(2026, 11, 9));
        SetWindowField(window, "month", new DateTime(2026, 11, 1));
        InvokeStartupRender(window, "RenderUsage");
        InvokeStartupRender(window, "RenderAccount");
        InvokeStartupRender(window, "RenderCalendar");
        ((TextBlock)window.FindName("StatusText")).Text = "샘플 일정 · 실제 Google 연동 아님";
        ((TextBlock)window.FindName("StatusText")).ToolTip = "미리보기의 일정 변경은 메모리에만 저장됩니다. 창을 닫으면 종료합니다.";
        ((Button)window.FindName("ConnectButton")).IsEnabled = false;
        ((Button)window.FindName("FooterRefreshButton")).IsEnabled = false;
        window.Closed += (_, _) => app.Shutdown();
        window.Show();
        window.UpdateLayout();
        Capture(window, "google-user-preview");
        if (smoke) app.Dispatcher.BeginInvoke(new Action(window.Close));
        app.Run();
        return 0;
    }

    private static void GoogleMainWindowCrudChecks(MainWindow window, FakeGoogleCalendarService google)
    {
        Exception? automationFailure = null;
        void DuringEditor(Action<GoogleEventDialog> action) => window.Dispatcher.BeginInvoke(new Action(() =>
        {
            var editor = Application.Current.Windows.OfType<GoogleEventDialog>().Single();
            try { action(editor); }
            catch (Exception exception) { automationFailure = exception; editor.Close(); }
        }));
        DuringEditor(editor =>
        {
            ((TextBox)editor.FindName("TitleInput")).Text = "Created through widget";
            ((Button)editor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        ((Button)window.FindName("GoogleAddButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (automationFailure is not null) throw automationFailure;
        Check(google.Saves == 1 && google.State.Events.Any(value => value.Summary == "Created through widget"),
            "Google widget add action saves through injected calendar service");
        Button Row(string title) => GoogleListRow(window, title).Children.OfType<Button>().Single(button => button.Name == "GoogleEventEditButton");
        DuringEditor(editor =>
        {
            ((TextBox)editor.FindName("TitleInput")).Text = "Updated through widget";
            ((Button)editor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Row("Created through widget").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (automationFailure is not null) throw automationFailure;
        Check(google.Saves == 2 && google.State.Events.Any(value => value.Summary == "Updated through widget") &&
            google.State.Events.All(value => value.Summary != "Created through widget"),
            "Google widget detail row edits the selected event through service");
        DuringEditor(editor =>
        {
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var confirm = Application.Current.Windows.OfType<GoogleActionConfirmDialog>().Single();
                ((Button)confirm.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            ((Button)editor.FindName("DeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Row("Updated through widget").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (automationFailure is not null) throw automationFailure;
        Check(google.Deletes == 1 && google.State.Events.All(value => value.Summary != "Updated through widget"),
            "Google widget confirmed delete removes only selected event through service");
    }

    private static async Task GoogleEditorChecks(MainWindow owner, IReadOnlyList<GoogleCalendarInfo> calendars)
    {
        GoogleEventDialogRequest? captured = null;
        var calls = 0;
        var original = FakeGoogleCalendarService.Event("editable", "Original", new(2026, 11, 9), new(2026, 11, 12));
        var dialog = new GoogleEventDialog(calendars, original, new(2026, 11, 9), (request, _) =>
        {
            captured = request;
            calls++;
            throw new GoogleCalendarConflictException();
        }) { Owner = owner };
        dialog.Show();
        await dialog.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        dialog.UpdateLayout();
        Capture(dialog, "google-editor-initial-fixture");
        Check(Descendants<TextBlock>((ComboBox)dialog.FindName("CalendarInput")).Any(text => text.Text == "Fixture calendar"),
            "Google editor calendar selector presents the calendar name");
        TextBox Input(string name) => (TextBox)dialog.FindName(name);
        var save = (Button)dialog.FindName("SaveButton");
        Check(((DatePicker)dialog.FindName("StartDateInput")).SelectedDate == new DateTime(2026, 11, 9) &&
            ((DatePicker)dialog.FindName("EndDateInput")).SelectedDate == new DateTime(2026, 11, 11),
            "Google all-day editor shows the last inclusive date from exclusive API end");
        Input("TitleInput").Text = "Keep my edited title";
        Input("DescriptionInput").Text = "Keep my edited description";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => calls == 1 && save.IsEnabled, "Google conflict restores editor controls");
        Check(dialog.IsVisible && Input("TitleInput").Text == "Keep my edited title" && Input("DescriptionInput").Text == "Keep my edited description" &&
            captured?.Event.EndDate == new DateOnly(2026, 11, 12) && captured.Scope == GoogleEventDialogScope.ThisEvent,
            "Google save conflict preserves unsaved fields and exclusive end with explicit instance scope");
        ((DatePicker)dialog.FindName("EndDateInput")).SelectedDate = new DateTime(2026, 11, 8);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls == 1 && ((TextBlock)dialog.FindName("StatusText")).Text.Length > 0,
            "Google editor rejects reversed all-day dates without invoking save");
        Capture(dialog, "google-editor-conflict-fixture");
        dialog.Close();

        dialog = new GoogleEventDialog(calendars, original with { CanWrite = false }, new(2026, 11, 9), (_, _) =>
            throw new InvalidOperationException("Read-only callback must not run")) { Owner = owner };
        dialog.Show();
        Check(!((FrameworkElement)dialog.FindName("EditorPanel")).IsEnabled && ((Button)dialog.FindName("SaveButton")).Visibility == Visibility.Collapsed &&
            ((Button)dialog.FindName("DeleteButton")).Visibility == Visibility.Collapsed,
            "Google read-only event displays its content with write actions hidden");
        Capture(dialog, "google-editor-readonly-fixture");
        dialog.Close();

        captured = null;
        dialog = new GoogleEventDialog(calendars, original with { RecurringEventId = "master" }, new(2026, 11, 9), (request, _) =>
        {
            captured = request;
            throw new InvalidOperationException("Fixture failure keeps dialog open");
        }, _ => Task.FromResult(original with { Id = "master", ETag = "\"master-etag\"", Summary = "Whole series", Recurrence = ["RRULE:FREQ=WEEKLY"] })) { Owner = owner };
        dialog.Show();
        var scope = (ComboBox)dialog.FindName("ScopeInput");
        scope.SelectedIndex = 1;
        await WaitUntilAsync(() => ((TextBox)dialog.FindName("TitleInput")).Text == "Whole series", "Google series scope loads master fields");
        ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(captured is { Scope: GoogleEventDialogScope.EntireSeries, Event.Id: "master", Event.ETag: "\"master-etag\"" },
            "Google entire-series save targets master ID and master ETag");
        dialog.Close();

        using var began = new SemaphoreSlim(0);
        var cancellationObserved = false;
        dialog = new GoogleEventDialog(calendars, original, new(2026, 11, 9), async (_, token) =>
        {
            began.Release();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancellationObserved = true; throw; }
        }) { Owner = owner };
        dialog.Show();
        ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await began.WaitAsync();
        ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => cancellationObserved && !dialog.IsVisible, "Google cancel aborts pending save and closes after completion");
    }

    private sealed class FakeGoogleCalendarService : IGoogleCalendarService
    {
        public GoogleCalendarState State { get; private set; } = new()
        {
            Connected = true, IsConfigured = true, AccountEmail = "google-fixture@example.invalid", SelectedCalendarIds = new HashSet<string> { "fixture-calendar" },
            Calendars = [new() { Id = "fixture-calendar", Summary = "Fixture calendar", AccessRole = "owner", Primary = true },
                new() { Id = "readonly-calendar", Summary = "Read only", AccessRole = "reader" }],
            Events = [Event("span", "Week crossing span", new(2026, 11, 6), new(2026, 11, 12)),
                Event("one", "Google fixture one", new(2026, 11, 9), new(2026, 11, 10)),
                Event("two", "Google fixture two", new(2026, 11, 9), new(2026, 11, 10)),
                Event("three", "Google fixture three", new(2026, 11, 9), new(2026, 11, 10)),
                Event("icon", "Google single-day icon", new(2026, 11, 16), new(2026, 11, 17))]
        };
        public bool Disposed { get; private set; }
        public Exception? SelectionFailure { get; set; }
        public int Saves { get; private set; }
        public int Deletes { get; private set; }
        public int EventGets { get; private set; }
        public GoogleCalendarEvent? LastDeleted { get; private set; }
        public Func<GoogleCalendarEvent, CancellationToken, Task>? DeleteBehavior { get; set; }
        public int Connects { get; private set; }
        public int Initializations { get; private set; }
        public int BrowserCallbacks { get; private set; }
        public bool InvokeBrowserFixture { get; set; }
        public Exception? ConnectFailure { get; set; }
        public GoogleCalendarState? StateAfterInitialize { get; set; }
        public event EventHandler? Changed;
        public void SetFixtureState(GoogleCalendarState value) => State = value;
        public static GoogleCalendarEvent Event(string id, string title, DateOnly start, DateOnly end) => new()
        {
            Id = id, ETag = "\"fixture-etag\"", CalendarId = "fixture-calendar", Summary = title, AllDay = true, CanWrite = true, StartDate = start, EndDate = end
        };
        public void SetConnection(bool connected, bool configured)
        {
            State = State with { Connected = connected, IsConfigured = configured };
            Changed?.Invoke(this, EventArgs.Empty);
        }
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Initializations++;
            if (StateAfterInitialize is not null)
            {
                State = StateAfterInitialize;
                StateAfterInitialize = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            return Task.CompletedTask;
        }
        public Task ConnectAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(openBrowser);
            Connects++;
            BrowserCallbacks++;
            if (InvokeBrowserFixture) openBrowser(new Uri("https://accounts.google.com/fixture-only"));
            if (ConnectFailure is not null) throw ConnectFailure;
            State = State with { Connected = true };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public List<(DateTimeOffset Start, DateTimeOffset End)> RefreshRequests { get; } = [];
        public Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task>? RefreshBehavior { get; set; }
        public Task RefreshAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
        {
            RefreshRequests.Add((start, end));
            return RefreshBehavior?.Invoke(start, end, cancellationToken) ?? Task.CompletedTask;
        }
        public Task<GoogleCalendarEvent> SaveAsync(GoogleCalendarEvent value, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (value.Id.Length == 0) value = value with { Id = value.ClientGeneratedId, ETag = "\"saved-fixture\"" };
            State = State with { Events = State.Events.Where(e => e.Id != value.Id).Append(value).ToArray() };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(value);
        }
        public async Task DeleteAsync(GoogleCalendarEvent value, CancellationToken cancellationToken = default)
        {
            Deletes++;
            LastDeleted = value;
            if (DeleteBehavior is not null) await DeleteBehavior(value, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            State = State with { Events = State.Events.Where(e => e.Id != value.Id).ToArray() };
            Changed?.Invoke(this, EventArgs.Empty);
        }
        public Task<GoogleCalendarEvent> GetEventAsync(string calendarId, string eventId, CancellationToken cancellationToken = default)
        {
            EventGets++;
            return Task.FromResult(State.Events.Single(e => e.CalendarId == calendarId && e.Id == eventId));
        }
        public Task SelectCalendarsAsync(IEnumerable<string> calendarIds, CancellationToken cancellationToken = default)
        {
            if (SelectionFailure is not null) throw SelectionFailure;
            State = State with { SelectedCalendarIds = calendarIds.ToHashSet() };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = State with { Connected = false, Events = [] };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public void Dispose() => Disposed = true;
    }
}
