using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static Grid GoogleListRow(MainWindow window, string title) =>
        ((StackPanel)window.FindName("CalendarEventList")).Children.OfType<Grid>()
            .Single(row => row.Tag is GoogleCalendarEvent value && value.Summary == title);

    private static Button GoogleDeleteButton(MainWindow window, string title) =>
        GoogleListRow(window, title).Children.OfType<Button>().Single(button => button.Name == "GoogleEventDeleteButton");

    private static Task InvokeGoogleDelete(MainWindow window, GoogleCalendarEvent value) =>
        (Task)typeof(MainWindow).GetMethod("DeleteGoogleEventAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [value])!;

    private static bool GoogleWindowFieldIsNull(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null;

    private static void DuringGoogleConfirmation(MainWindow owner, Action<GoogleActionConfirmDialog> action)
    {
        owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            var confirm = Application.Current.Windows.OfType<GoogleActionConfirmDialog>().Single();
            try { action(confirm); }
            catch { confirm.Close(); throw; }
        }));
    }

    private static async Task GoogleDirectDeleteChecks(MainWindow window, FakeGoogleCalendarService google)
    {
        var original = google.State;
        var value = FakeGoogleCalendarService.Event("direct-delete", "Direct list delete fixture", new(2026, 11, 9), new(2026, 11, 12));
        var recurring = value with { Id = "instance-20261109", Summary = "Recurring list fixture", RecurringEventId = "series-master" };
        var readOnly = value with { Id = "readonly", Summary = "Read only fixture", CanWrite = false };
        var readerCalendar = value with { Id = "reader-role", Summary = "Reader calendar fixture", CalendarId = "readonly-calendar" };
        var master = value with { Id = "series-master", Summary = "Series master fixture", Recurrence = ["RRULE:FREQ=WEEKLY"] };
        var holiday = value with { Id = "holiday-delete", Summary = "Holiday list fixture", CalendarId = "ko.south_korea#holiday@group.v.calendar.google.com" };
        google.SetFixtureState(original with { Events = original.Events.Concat([value, recurring, readOnly, readerCalendar, holiday, master]).ToArray() });
        InvokeStartupRender(window, "RenderCalendar");
        window.UpdateLayout();
        var row = GoogleListRow(window, value.Summary);
        var edit = row.Children.OfType<Button>().Single(button => button.Name == "GoogleEventEditButton");
        var trash = GoogleDeleteButton(window, value.Summary);
        Check(row.ColumnDefinitions.Count == 2 && row.ColumnDefinitions[1].Width.Value == 22 &&
            trash.ActualWidth == 22 && trash.ActualHeight == 22 &&
            !OverlapsInterior(VisualBounds(edit, row), VisualBounds(trash, row)),
            "Google writable list row reserves a separate nonoverlapping 22 DIP trash target");
        Check(new[] { readOnly, readerCalendar, holiday, master }.All(item =>
            GoogleListRow(window, item.Summary).Children.OfType<Button>().All(button => button.Name != "GoogleEventDeleteButton")) &&
            ((StackPanel)window.FindName("CalendarEventList")).Children.OfType<Grid>().Where(item => item.Tag is not GoogleCalendarEvent)
                .All(item => !Descendants<Button>(item).Any()),
            "read-only reader-calendar holiday series-master and Codex rows have no direct delete action");
        var before = google.Deletes;
        ((Slider)window.FindName("OpacitySlider")).Value = .55;
        var detailScroll = (ScrollViewer)window.FindName("CalendarDetailScroll");
        detailScroll.ScrollToVerticalOffset(72);
        window.UpdateLayout();
        Capture(window, "google-list-direct-actions-background-55");
        detailScroll.ScrollToTop();
        DuringGoogleConfirmation(window, confirm =>
        {
            Check(!Application.Current.Windows.OfType<GoogleEventDialog>().Any(), "list trash opens confirmation directly without edit dialog");
            var description = ((TextBlock)confirm.FindName("DescriptionText")).Text;
            Check(description.Contains(value.Summary) && description.Contains("2026-11-09") && description.Contains("2026-11-11"),
                "direct delete confirmation identifies event title and inclusive actual date range");
            CheckOpaqueGoogleWindow(window, confirm, "direct delete confirmation");
            Capture(confirm, "google-direct-delete-confirm-background-55");
            ((Button)confirm.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        trash.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !WindowField<bool>(window, "googleDeleting"), "cancelled list delete finishes");
        Check(google.Deletes == before && google.State.Events.Contains(value), "cancelled direct delete sends zero DELETE calls and keeps the event");

        SetWindowField(window, "selectedGoogleEvent", CalendarEventPresentation.Key(value));
        google.DeleteBehavior = (_, _) => throw new IOException("Fixture direct delete failure");
        DuringGoogleConfirmation(window, confirm => ((Button)confirm.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        GoogleDeleteButton(window, value.Summary).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !WindowField<bool>(window, "googleDeleting"), "failed direct delete restores controls");
        Check(google.Deletes == before + 1 && google.State.Events.Contains(value) && GoogleListRow(window, value.Summary).IsVisible &&
            WindowField<string>(window, "selectedGoogleEvent") == CalendarEventPresentation.Key(value) &&
            ((Button)window.FindName("GoogleRefreshButton")).ToolTip.ToString()!.Contains("Fixture direct delete failure"),
            "direct delete failure keeps visible row selection and a visible actionable error");
        google.DeleteBehavior = null;

        var gets = google.EventGets;
        DuringGoogleConfirmation(window, confirm =>
        {
            Check(((TextBlock)confirm.FindName("DescriptionText")).Text.Contains("이 일정만"),
                "recurring direct delete confirmation explicitly identifies instance-only scope");
            ((Button)confirm.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        GoogleDeleteButton(window, recurring.Summary).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !WindowField<bool>(window, "googleDeleting"), "confirmed recurring list delete finishes");
        Check(google.Deletes == before + 2 && google.LastDeleted?.Id == recurring.Id && google.LastDeleted.ETag == recurring.ETag &&
            google.EventGets == gets && google.State.Events.All(item => item.Id != recurring.Id) && google.State.Events.Contains(value),
            "confirmed recurring list trash sends exactly one DELETE with original instance ID ETag and never loads master");

        var beforeAccountChange = google.Deletes;
        DuringGoogleConfirmation(window, confirm =>
        {
            google.SetFixtureState(google.State with { AccountEmail = "different-fixture@example.invalid" });
            ((Button)confirm.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        GoogleDeleteButton(window, value.Summary).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !WindowField<bool>(window, "googleDeleting"), "account change invalidates direct delete");
        Check(google.Deletes == beforeAccountChange && google.State.Events.Contains(value), "account change during confirmation prevents DELETE");
        google.SetFixtureState(google.State with { AccountEmail = original.AccountEmail });

        DuringGoogleConfirmation(window, confirm =>
        {
            google.SetFixtureState(google.State with { Events = google.State.Events.Select(item => item.Id == value.Id ? item with { ETag = "changed-etag" } : item).ToArray() });
            ((Button)confirm.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        GoogleDeleteButton(window, value.Summary).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !WindowField<bool>(window, "googleDeleting"), "changed ETag invalidates direct delete");
        Check(google.Deletes == beforeAccountChange && google.State.Events.Any(item => item.Id == value.Id),
            "event changed during confirmation prevents stale DELETE");
        google.SetFixtureState(google.State with { Events = google.State.Events.Select(item => item.Id == value.Id ? value : item).ToArray() });
        InvokeStartupRender(window, "RenderCalendar");

        foreach (var ineligible in new[] { readOnly, readerCalendar, holiday, master }) await InvokeGoogleDelete(window, ineligible);
        Check(google.Deletes == beforeAccountChange && !Application.Current.Windows.OfType<GoogleActionConfirmDialog>().Any(),
            "ineligible direct delete calls cannot create confirmation or service requests");

        SetWindowField(window, "updateApplying", true);
        await InvokeGoogleDelete(window, value);
        SetWindowField(window, "updateApplying", false);
        Check(google.Deletes == beforeAccountChange && !Application.Current.Windows.OfType<GoogleActionConfirmDialog>().Any(),
            "update application blocks direct delete before confirmation and service call");

        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        google.DeleteBehavior = (_, token) => pending.Task.WaitAsync(token);
        DuringGoogleConfirmation(window, confirm => ((Button)confirm.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        GoogleDeleteButton(window, value.Summary).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => google.Deletes == beforeAccountChange + 1 && WindowField<bool>(window, "googleDeleting"), "direct delete remains pending in fake service");
        Check(new[] { "GoogleAddButton", "GoogleRefreshButton", "GoogleConnectButton" }.All(name => !((Button)window.FindName(name)).IsEnabled) &&
            GoogleListRow(window, value.Summary).Children.OfType<Button>().All(button => !button.IsEnabled),
            "pending direct delete disables connection refresh add edit and trash actions");
        await InvokeGoogleDelete(window, value);
        Check(google.Deletes == beforeAccountChange + 1 && !Application.Current.Windows.OfType<GoogleActionConfirmDialog>().Any(),
            "duplicate activation during pending deletion causes no second dialog or DELETE");
        var coordinator = WindowField<UpdateCoordinator>(window, "updates");
        var updateService = (UpdateChecks.FakeUpdateService)typeof(UpdateCoordinator)
            .GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        updateService.Offered = new AppUpdate("99.0.0");
        typeof(UpdateCoordinator).GetField("nextCheck", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, long.MinValue);
        await coordinator.CheckIfDueAsync();
        ((Button)window.FindName("UpdateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => coordinator.IsDownloaded && !coordinator.IsBusy, "fake updater finishes download during pending delete");
        Check(updateService.Downloads == 1 && updateService.Restarts == 0 && !WindowField<bool>(window, "updateApplying") &&
            !WindowField<bool>(window, "closing") && !GoogleWindowFieldIsNull(window, "updateRetry"),
            "pending direct delete defers downloaded update restart and keeps widget open with retry");
        pending.SetResult();
        await WaitUntilAsync(() => !WindowField<bool>(window, "googleDeleting"), "successful direct delete completes pending operation");
        Check(google.State.Events.All(item => item.Id != value.Id) && GoogleWindowFieldIsNull(window, "selectedGoogleEvent") &&
            ((Button)window.FindName("GoogleAddButton")).IsEnabled,
            "successful direct delete removes selected row clears selection and restores actions");
        google.DeleteBehavior = null;
        google.SetFixtureState(original);
        InvokeStartupRender(window, "RenderCalendar");
    }

    private static void CheckOpaqueGoogleWindow(MainWindow main, Window dialog, string name)
    {
        dialog.UpdateLayout();
        Check(BackgroundOpacity.GetValue(main) == .55 && main.Opacity == 1 && BackgroundOpacity.GetValue(dialog) == 1 && dialog.Opacity == 1,
            name + " stays opaque while main background remains at 55 percent");
        CheckAlpha(RenderPixels(dialog), 10, dialog.ActualHeight / 2, 1, name + " visible background");
    }

    private static async Task GoogleOpaqueDialogChecks(MainWindow main, FakeGoogleCalendarService google)
    {
        var slider = (Slider)main.FindName("OpacitySlider");
        slider.Value = .55;
        var account = new GoogleAccountDialog(google, _ => throw new InvalidOperationException("Fixture must not open browser")) { Owner = main };
        account.Show();
        CheckOpaqueGoogleWindow(main, account, "Google account dialog");
        Capture(account, "google-account-background-55");
        account.Close();
        var credit = new ResetCredit("opaque-credit", "codexRateLimits", "available", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), "Opacity fixture", null);
        var reset = new ResetConfirmDialog("fixture@example.invalid", "plus", credit) { Owner = main };
        reset.Show();
        CheckOpaqueGoogleWindow(main, reset, "Codex reset dialog");
        reset.Close();
        var editor = new GoogleEventDialog(google.State.Calendars, null, new(2026, 11, 9), (_, _) => Task.CompletedTask) { Owner = main };
        editor.Show();
        await editor.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        CheckOpaqueGoogleWindow(main, editor, "Google event editor");
        var combo = (ComboBox)editor.FindName("CalendarInput");
        combo.IsDropDownOpen = true;
        await editor.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        var surface = (Border)popup.Child;
        Check(popup.IsOpen && surface.IsVisible && BackgroundOpacity.GetValue(surface) == 1 && surface.Background.Opacity == 1,
            "Google calendar ComboBox popup has a fully opaque visible background at translucent main");
        CheckAlpha(RenderPixels(surface), 3, surface.ActualHeight / 2, 1, "Google calendar ComboBox popup background");
        CaptureGoogleElement(surface, "google-combobox-background-55");
        combo.IsDropDownOpen = false;
        foreach (var factor in new[] { .75, 1, .55 })
        {
            slider.Value = factor;
            Check(BackgroundOpacity.GetValue(editor) == 1 && BackgroundOpacity.GetValue(main) == factor,
                "live main opacity adjustment preserves independent Google editor opacity");
        }
        editor.Close();
    }

    private static async Task GoogleDeleteCloseChecks(MainWindow main, FakeGoogleCalendarService google)
    {
        var value = google.State.Events.First(item => item.CanWrite && item.AllDay);
        SetWindowField(main, "selectedDate", CalendarEventPresentation.Dates(value)!.Value.Start.ToDateTime(TimeOnly.MinValue));
        SetWindowField(main, "googleError", null);
        InvokeStartupRender(main, "RenderCalendar");
        var cancelled = false;
        var before = google.Deletes;
        google.DeleteBehavior = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        };
        DuringGoogleConfirmation(main, confirm => ((Button)confirm.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        GoogleDeleteButton(main, value.Summary).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => google.Deletes == before + 1 && WindowField<bool>(main, "googleDeleting"), "fake direct delete is pending before closing");
        main.Close();
        await WaitUntilAsync(() => cancelled && !WindowField<bool>(main, "googleDeleting"), "closing cancels pending direct delete through window lifetime token");
        Check(google.State.Events.Contains(value) && GoogleWindowFieldIsNull(main, "googleError"),
            "close cancellation retains fake event and does not surface a deletion error");
    }
}
