using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static void CheckNativeIcons(string executable, IntPtr window)
    {
        var expected = new IconBitmapDecoder(new Uri(Path.Combine(Directory.GetCurrentDirectory(), "src", "UsageRemaining.Widget", "Assets", "CodexPulse.ico")),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        var extracted = ExtractIconEx(executable, 0, large, small, 1);
        Console.WriteLine($"ICON: extracted={extracted}, large={large[0] != IntPtr.Zero}, small={small[0] != IntPtr.Zero}");
        Check(extracted > 0 && large[0] != IntPtr.Zero && small[0] != IntPtr.Zero, "published EXE contains native large and small icons");
        try
        {
            CheckIconArtwork(large[0], expected, "exe-icon");
            CheckIconArtwork(small[0], expected, "exe-icon-small");
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
        }
        var windowIcon = SendMessage(window, 0x007F, new IntPtr(1), IntPtr.Zero);
        CheckIconArtwork(windowIcon, expected, "window-icon");
    }

    private static void CheckIconArtwork(IntPtr handle, IconBitmapDecoder expected, string label)
    {
        Check(handle != IntPtr.Zero, label + " is available to Windows");
        var actual = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        var frame = expected.Frames.Single(f => f.PixelWidth == actual.PixelWidth && f.PixelHeight == actual.PixelHeight);
        var source = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        var result = new FormatConvertedBitmap(actual, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        var received = new byte[pixels.Length];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        result.CopyPixels(received, result.PixelWidth * 4, 0);
        var difference = pixels.Select((value, index) => Math.Abs(value - received[index])).Average();
        Check(difference < 3, label + " matches selected C artwork");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(actual));
        Directory.CreateDirectory("artifacts");
        using var output = File.Create(Path.Combine("artifacts", label + ".png"));
        encoder.Save(output);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
