using System.Text.Json;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;

namespace MqttProbe.Core.Tests.Services.Configuration;

[TestFixture]
public class SparkplugTopicGroupingPreferenceTests
{
    private string _configPath = null!;
    private SettingsDocument _document = null!;
    private PreferenceSettings _preferences = null!;

    [SetUp]
    public void Setup()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"mqttprobe_grouping_{Guid.NewGuid()}.json");
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
    public void UiPreferences_GroupingDefaultsOn()
    {
        new UiPreferences().GroupSparkplugTopicsByNodeAndDevice.Should().BeTrue();
    }

    [Test]
    public async Task LoadAsync_DefaultsGroupingOn_WhenUiKeyAbsent()
    {
        await File.WriteAllTextAsync(_configPath, """{"connections":[],"ui":{"theme":"light"}}""");

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();

        reloaded.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().BeTrue();
        reloaded.Config.Ui.Theme.Should().Be("light");
    }

    [Test]
    public async Task LoadAsync_DefaultsGroupingOn_WhenUiObjectAbsent()
    {
        await File.WriteAllTextAsync(_configPath, """{"connections":[]}""");

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();

        reloaded.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().BeTrue();
    }

    [Test]
    public async Task LoadAsync_DefaultsGroupingOn_WhenConfigFileAbsent()
    {
        using var fresh = new SettingsDocument(_configPath);

        var created = await new SettingsLoader(fresh, new ConnectionSecrets(null, null), false, null).LoadAsync();

        created.Should().BeFalse("the loader created a fresh config");
        fresh.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LoadAsync_PreservesExplicitGroupingValue(bool value)
    {
        var json = "{\"connections\":[],\"ui\":{\"groupSparkplugTopicsByNodeAndDevice\":"
            + (value ? "true" : "false")
            + "}}";
        await File.WriteAllTextAsync(_configPath, json);

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();

        reloaded.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().Be(value);
    }

    [Test]
    public async Task SetGroupingAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _preferences.UiPreferencesChanged += () => fired = true;

        await _preferences.SetGroupSparkplugTopicsByNodeAndDeviceAsync(true);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetGroupingAsync_DoesNotRaiseSparkplugSettingsChanged()
    {
        var fired = false;
        _preferences.SparkplugSettingsChanged += () => fired = true;

        await _preferences.SetGroupSparkplugTopicsByNodeAndDeviceAsync(true);

        fired.Should().BeFalse("grouping is a UI presentation preference");
    }

    [Test]
    public async Task SetGroupingAsync_PersistsOnThenOffAcrossReloads()
    {
        await _preferences.SetGroupSparkplugTopicsByNodeAndDeviceAsync(true);
        _document.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().BeTrue();

        using (var reloadedOn = new SettingsDocument(_configPath))
        {
            await new SettingsLoader(reloadedOn, new ConnectionSecrets(null, null), false, null).LoadAsync();
            reloadedOn.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().BeTrue();
        }

        await _preferences.SetGroupSparkplugTopicsByNodeAndDeviceAsync(false);
        _document.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().BeFalse();

        using var reloadedOff = new SettingsDocument(_configPath);
        await new SettingsLoader(reloadedOff, new ConnectionSecrets(null, null), false, null).LoadAsync();
        reloadedOff.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().BeFalse();
    }

    [Test]
    public async Task SetGroupingAsync_PreservesOtherUiPreferences()
    {
        await _preferences.SetThemeAsync("light");
        await _preferences.SetFontProfileAsync(FontProfiles.Accessible);
        await _preferences.SetAutoResubscribeAsync(false);
        await _preferences.DismissHintAsync("topic-tree-hint");

        await _preferences.SetGroupSparkplugTopicsByNodeAndDeviceAsync(true);

        _document.Config.Ui.Theme.Should().Be("light");
        _document.Config.Ui.FontProfile.Should().Be(FontProfiles.Accessible);
        _document.Config.Ui.AutoResubscribe.Should().BeFalse();
        _document.Config.Ui.DismissedHints.Should().Contain("topic-tree-hint");
        _document.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().BeTrue();

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();
        reloaded.Config.Ui.Theme.Should().Be("light");
        reloaded.Config.Ui.FontProfile.Should().Be(FontProfiles.Accessible);
        reloaded.Config.Ui.AutoResubscribe.Should().BeFalse();
        reloaded.Config.Ui.GroupSparkplugTopicsByNodeAndDevice.Should().BeTrue();
    }

    [Test]
    public async Task SetGroupingAsync_WritesCamelCaseKeyUnderUi()
    {
        await _preferences.SetGroupSparkplugTopicsByNodeAndDeviceAsync(true);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_configPath));

        var ui = doc.RootElement.GetProperty("ui");
        ui.GetProperty("groupSparkplugTopicsByNodeAndDevice").GetBoolean().Should().BeTrue();
    }
}
