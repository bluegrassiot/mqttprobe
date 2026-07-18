using MqttProbe.Desktop.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Security;

[TestFixture]
public class MacKeychainQueryEntriesTests
{
    [TestCase(false, 3)]
    [TestCase(true, 5)]
    public void Create_ReturnsOnlyInitializedEntries(bool returnData, int expectedCount)
    {
        var (keys, values) = MacKeychainQueryEntries.Create(
            new IntPtr(1),
            new IntPtr(2),
            new IntPtr(3),
            new IntPtr(4),
            new IntPtr(5),
            new IntPtr(6),
            new IntPtr(7),
            new IntPtr(8),
            new IntPtr(9),
            new IntPtr(10),
            returnData);

        Assert.That(keys, Has.Length.EqualTo(expectedCount));
        Assert.That(values, Has.Length.EqualTo(expectedCount));
        Assert.That(keys, Has.None.EqualTo(IntPtr.Zero));
        Assert.That(values, Has.None.EqualTo(IntPtr.Zero));
    }
}
