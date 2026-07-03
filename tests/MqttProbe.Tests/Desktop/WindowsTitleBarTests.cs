using MqttProbe.Desktop.Interop;
using NUnit.Framework;

namespace MqttProbe.Shared.Tests.Desktop;

[TestFixture]
public class WindowsTitleBarTests
{
    // COLORREF is 0x00BBGGRR — bytes reversed relative to the #RRGGBB hex.
    [TestCase("#1E293B", 0x003B291Eu)] // appbar SlateDark
    [TestCase("#F1F5F9", 0x00F9F5F1u)] // caption text OffWhite
    [TestCase("FFFFFF", 0x00FFFFFFu)]  // leading # optional
    public void HexToColorRef_converts_rgb_hex_to_colorref(string hex, uint expected)
    {
        Assert.That(WindowsTitleBar.HexToColorRef(hex), Is.EqualTo(expected));
    }
}
