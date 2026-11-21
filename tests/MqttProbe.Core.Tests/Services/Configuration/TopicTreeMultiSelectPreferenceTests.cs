using System.Text.Json;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;

namespace MqttProbe.Core.Tests.Services.Configuration;

[TestFixture]
public class TopicTreeMultiSelectPreferenceTests
{
    private string _configPath = null!;
    private SettingsDocument _document = null!;
    private PreferenceSettings _preferences = null!;

    [SetUp]
    public void Setup()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"mqttprobe_multiselect_{Guid.NewGuid()}.json");
        if (File.Exists(_configPath)) File.Delete(_configPath);
        _document = new SettingsDocument(_configPath);
        _preferences = new PreferenceSettings(_document);
    }

    [TearDown]
    public void TearDown()
    {
        _document.Dispose();
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Test]
    public void UiPreferences_MultiSelectDefaultsOn()
    {
        new UiPreferences().TopicTreeMultiSelect.Should().BeTrue();
    }

    [Test]
    public async Task LoadAsync_DefaultsMultiSelectOn_WhenUiKeyAbsent()
    {
        await File.WriteAllTextAsync(_configPath, """{"connections":[],"ui":{"theme":"light"}}""");

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();

        reloaded.Config.Ui.TopicTreeMultiSelect.Should().BeTrue();
        reloaded.Config.Ui.Theme.Should().Be("light");
    }

    [Test]
    public async Task LoadAsync_PreservesExplicitMultiSelectOff()
    {
        await File.WriteAllTextAsync(
            _configPath, """{"connections":[],"ui":{"topicTreeMultiSelect":false}}""");

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();

        reloaded.Config.Ui.TopicTreeMultiSelect.Should().BeFalse();
    }

    [Test]
    public async Task SetMultiSelectAsync_PersistsTheChoice()
    {
        await _preferences.SetTopicTreeMultiSelectAsync(false);
        _document.Config.Ui.TopicTreeMultiSelect.Should().BeFalse();

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();
        reloaded.Config.Ui.TopicTreeMultiSelect.Should().BeFalse();
    }

    [Test]
    public async Task SetMultiSelectAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _preferences.UiPreferencesChanged += () => fired = true;

        await _preferences.SetTopicTreeMultiSelectAsync(false);

        fired.Should().BeTrue();
        _document.Config.Ui.TopicTreeMultiSelect.Should().BeFalse();
    }

    [Test]
    public async Task SetMultiSelectAsync_WritesCamelCaseKeyUnderUi()
    {
        await _preferences.SetTopicTreeMultiSelectAsync(false);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_configPath));

        var ui = doc.RootElement.GetProperty("ui");
        ui.GetProperty("topicTreeMultiSelect").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task SetMultiSelectAsync_LeavesOtherUiPreferencesAlone()
    {
        await _preferences.SetThemeAsync("light");
        await _preferences.SetGroupSparkplugTopicsByNodeAndDeviceAsync(false);

        await _preferences.SetTopicTreeMultiSelectAsync(false);

        _document.Config.Ui.Theme.Should().Be("light");
        _document.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().BeFalse();
        _document.Config.Ui.TopicTreeMultiSelect.Should().BeFalse();
    }
}
