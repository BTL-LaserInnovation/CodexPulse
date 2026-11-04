using System.Runtime.InteropServices;
using System.Text;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static IntPtr FindWidgetWindow(int processId)
    {
        var result = IntPtr.Zero;
        // A tray-only WPF window has a hidden owner, so Process.MainWindowHandle can be zero.
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != (uint)processId || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "Codex Pulse") return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumerateWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumerateWindow callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int maximum);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
