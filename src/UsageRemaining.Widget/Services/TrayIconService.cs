using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UsageRemaining.Widget.Services;

public sealed class TrayIconService : IDisposable
{
    public const uint IconId = 1;
    public const int CallbackMessage = 0x8001;
    private const uint NotifyAdd = 0;
    private const uint NotifyModify = 1;
    private const uint NotifyDelete = 2;
    private const uint NotifySetVersion = 4;
    private const uint NotifyMessage = 0x1;
    private const uint NotifyIcon = 0x2;
    private const uint NotifyTip = 0x4;
    private const uint NotifyShowTip = 0x80;
    private const int LeftButtonUp = 0x0202;
    private const int SelectNotification = 0x0400;
    private const int KeySelectNotification = 0x0401;
    private readonly Window owner;
    private readonly Action activate;
    private readonly Action<bool>? registrationChanged;
    private readonly HwndSource source;
    private readonly IntPtr window;
    private readonly uint taskbarCreatedMessage;
    private bool disposed;

    public TrayIconService(Window owner, Action activate, Action<bool>? registrationChanged = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(activate);
        owner.Dispatcher.VerifyAccess();
        this.owner = owner;
        this.activate = activate;
        this.registrationChanged = registrationChanged;
        window = new WindowInteropHelper(owner).EnsureHandle();
        source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("창의 메시지 소스를 찾을 수 없습니다.");
        taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        source.AddHook(WindowProc);
        owner.Closed += Owner_Closed;
        RegisterIcon();
    }

    public bool IsRegistered { get; private set; }

    private void RegisterIcon()
    {
        if (disposed) return;
        // WPF owns the window's icon. Borrow it without destroying the HICON.
        var icon = SendMessage(window, 0x007F, new IntPtr(1), IntPtr.Zero);
        if (icon == IntPtr.Zero) icon = SendMessage(window, 0x007F, IntPtr.Zero, IntPtr.Zero);
        var data = CreateData();
        data.Flags = NotifyMessage | NotifyIcon | NotifyTip | NotifyShowTip;
        data.CallbackMessage = CallbackMessage;
        data.Icon = icon;
        data.Tip = "Codex Pulse · 클릭하여 보기";
        // Modify also makes duplicate TaskbarCreated notifications harmless.
        IsRegistered = icon != IntPtr.Zero &&
            (ShellNotifyIcon(NotifyAdd, ref data) || ShellNotifyIcon(NotifyModify, ref data));
        if (IsRegistered)
        {
            data.TimeoutOrVersion = 4;
            ShellNotifyIcon(NotifySetVersion, ref data);
        }
        registrationChanged?.Invoke(IsRegistered);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (disposed) return IntPtr.Zero;
        if (taskbarCreatedMessage != 0 && (uint)message == taskbarCreatedMessage)
        {
            RegisterIcon();
        }
        else if (message == CallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64());
            var legacyClick = notification == LeftButtonUp && unchecked((uint)wParam.ToInt64()) == IconId;
            var version4Selection = (notification >> 16) == IconId &&
                ((notification & 0xFFFF) == SelectNotification || (notification & 0xFFFF) == KeySelectNotification);
            if (IsRegistered && (legacyClick || version4Selection)) activate();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = window,
        Id = IconId,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private void Owner_Closed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        owner.Dispatcher.VerifyAccess();
        disposed = true;
        var data = CreateData();
        ShellNotifyIcon(NotifyDelete, ref data);
        IsRegistered = false;
        owner.Closed -= Owner_Closed;
        source.RemoveHook(WindowProc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
