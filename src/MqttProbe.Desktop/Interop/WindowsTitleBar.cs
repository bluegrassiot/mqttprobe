using System.Runtime.InteropServices;

namespace MqttProbe.Desktop.Interop;

public static class WindowsTitleBar
{
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const string CaptionHex = "#1E293B";
    private const string CaptionTextHex = "#F1F5F9";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    public static void ApplyBrandTint(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsSupported())
        {
            return;
        }

        var caption = HexToColorRef(CaptionHex);
        var text = HexToColorRef(CaptionTextHex);
        _ = DwmSetWindowAttribute(windowHandle, DwmwaCaptionColor, ref caption, sizeof(uint));
        _ = DwmSetWindowAttribute(windowHandle, DwmwaTextColor, ref text, sizeof(uint));
    }

    private static bool IsSupported()
        => OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 22000;

    public static uint HexToColorRef(string hex)
    {
        var rgb = Convert.ToUInt32(hex.TrimStart('#'), 16);
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return (b << 16) | (g << 8) | r;
    }
}
