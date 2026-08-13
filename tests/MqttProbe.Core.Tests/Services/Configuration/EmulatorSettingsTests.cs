using MqttProbe.Core.Models.Emulation;
using MqttProbe.Core.Services.Configuration;

namespace MqttProbe.Core.Tests.Services.Configuration;

[TestFixture]
public class EmulatorSettingsTests
{
    private string _configPath = null!;
    private SettingsDocument _document = null!;
    private EmulatorSettings _emulators = null!;

    [SetUp]
    public void Setup()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"mqttprobe_settings_{Guid.NewGuid()}.json");
        if (File.Exists(_configPath)) File.Delete(_configPath);
        _document = new SettingsDocument(_configPath);
        _emulators = new EmulatorSettings(_document);
    }

    [TearDown]
    public void TearDown()
    {
        _document.Dispose();
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Test]
    public async Task AddEmulatorNodeAsync_RaisesEmulatorsChangedForTheConnection()
    {
        var connectionId = Guid.NewGuid();
        Guid? raisedFor = null;
        _emulators.EmulatorsChanged += id => raisedFor = id;

        await _emulators.AddEmulatorNodeAsync(connectionId, new EmulatorNodeConfig { NodeId = "n" });

        raisedFor.Should().Be(connectionId);
        _emulators.GetEmulatorNodes(connectionId).Should().ContainSingle(n => n.NodeId == "n");
    }
}
