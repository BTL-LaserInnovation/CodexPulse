using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private const uint TrayIconId = 1;

    private static void VerifyNativeTrayBeforeClose(IntPtr window)
    {
        Check(TrayRegistered(window), "notification area contains the widget C icon");
        Check((GetWindowLongPtr(window, -20).ToInt64() & 0x00040000) == 0,
            "widget native window has no app-window taskbar style");
    }

    private static void VerifyNativeTrayAfterClose(IntPtr window) =>
        Check(!TrayRegistered(window), "closing removes the native notification icon");

    private static async Task VerifyTrayAsync(MainWindow window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        VerifyNativeTrayBeforeClose(handle);
        Check(!window.ShowInTaskbar, "visible widget has no taskbar button");
        await VerifyTrayRestoreAsync(window, 0x0202, legacy: true);

        // Remove just this test window's icon, then simulate Explorer's restart notice.
        var data = new TrayNotifyIconData { Size = Marshal.SizeOf<TrayNotifyIconData>(), Window = handle, Id = TrayIconId };
        Check(Shell_NotifyIcon(2, ref data) && !TrayRegistered(handle), "test removes only its own notification icon");
        var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        Check(taskbarCreated != 0, "Explorer restart message is registered");
        SendMessage(handle, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
        await WaitUntilAsync(() => TrayRegistered(handle), "Explorer restart notice restores the notification icon");
        Check(!window.ShowInTaskbar, "restored notification icon keeps the taskbar button hidden");
    }

    private static async Task VerifyTrayRestoreAsync(MainWindow window, uint notification = 0x0401, bool legacy = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var topmost = window.Topmost;
        var opacity = BackgroundOpacity.GetValue(window);
        var height = window.Height;
        var left = window.Left;
        var top = window.Top;
        var expanded = ((FrameworkElement)window.FindName("ExpandedPanel")).Visibility;
        window.WindowState = WindowState.Minimized;
        await WaitUntilAsync(() => window.WindowState == WindowState.Minimized, "widget can be minimized before tray activation");
        SendTrayActivation(handle, notification, legacy);
        await WaitUntilAsync(() => window.WindowState == WindowState.Normal && window.IsActive,
            legacy ? "legacy tray click restores and activates the widget" : "keyboard tray selection restores and activates the widget");
        Check(window.Topmost == topmost && BackgroundOpacity.GetValue(window) == opacity && window.Opacity == 1 &&
            window.Height == height && ((FrameworkElement)window.FindName("ExpandedPanel")).Visibility == expanded &&
            Math.Abs(window.Left - left) < 1 && Math.Abs(window.Top - top) < 1 && !window.ShowInTaskbar,
            "tray activation preserves pin opacity collapse and position preferences");
    }

    private static async Task VerifyTrayModalAsync(MainWindow owner, ResetConfirmDialog dialog)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        Check(!IsWindowEnabled(ownerHandle), "confirmation keeps its owner disabled");
        dialog.WindowState = WindowState.Minimized;
        SendTrayActivation(ownerHandle, 0x0400);
        await WaitUntilAsync(() => dialog.WindowState == WindowState.Normal && dialog.IsActive,
            "tray selection restores the existing confirmation");
        Check(!IsWindowEnabled(ownerHandle) && !owner.IsActive &&
            Application.Current.Windows.OfType<ResetConfirmDialog>().Count() == 1,
            "tray activation preserves the modal confirmation and disabled owner");
    }

    private static void SendTrayActivation(IntPtr window, uint notification, bool legacy = false) =>
        SendMessage(window, (uint)TrayIconService.CallbackMessage,
            legacy ? new IntPtr(TrayIconId) : IntPtr.Zero,
            new IntPtr(legacy ? notification : notification | (TrayIconId << 16)));

    private static async Task VerifyTrayCloseAsync(MainWindow window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        var close = Descendants<Button>(window).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == "\uB2EB\uAE30");
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        VerifyNativeTrayAfterClose(handle);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Check(!window.IsVisible && !IsWindow(handle), "X closes the widget native window instead of hiding it");
        VerifyNativeTrayAfterClose(handle);
    }

    private static bool TrayRegistered(IntPtr window)
    {
        var identifier = new TrayNotifyIconIdentifier
        {
            Size = Marshal.SizeOf<TrayNotifyIconIdentifier>(), Window = window, Id = TrayIconId
        };
        return Shell_NotifyIconGetRect(ref identifier, out _) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayNotifyIconIdentifier { public int Size; public IntPtr Window; public uint Id; public Guid Guid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrayNotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref TrayNotifyIconIdentifier identifier, out TrayRect rect);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint message, ref TrayNotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
}
