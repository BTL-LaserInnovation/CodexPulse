using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class MainWindow
{
    private void ApplyCollapsedState()
    {
        Height = state.IsCollapsed ? desiredCompactHeight : 480;
        ExpandedPanel.Visibility = state.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CompactPanel.Visibility = state.IsCollapsed ? Visibility.Visible : Visibility.Collapsed;
        CollapseIcon.Data = Geometry.Parse(state.IsCollapsed ? "M 0,0 L 5,5 10,0" : "M 0,5 L 5,0 10,5");
        CollapseButton.ToolTip = state.IsCollapsed ? "펼치기" : "접기";
        System.Windows.Automation.AutomationProperties.SetName(CollapseButton, state.IsCollapsed ? "펼치기" : "접기");
    }

    private void ToggleCollapse_Click(object sender, RoutedEventArgs e)
    {
        state.IsCollapsed = !state.IsCollapsed;
        ApplyCollapsedState();
        ClampToWorkArea();
        SaveSettings();
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !WindowInteraction.CanDragFrom(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        try { DragMove(); ClampToWorkArea(); }
        catch (InvalidOperationException) when (closing || !IsVisible) { }
    }

    private void ClampToWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
        Left = Math.Clamp(double.IsFinite(Left) ? Left : topLeft.X, topLeft.X, Math.Max(topLeft.X, bottomRight.X - Width));
        Top = Math.Clamp(double.IsFinite(Top) ? Top : topLeft.Y, topLeft.Y, Math.Max(topLeft.Y, bottomRight.Y - Height));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
