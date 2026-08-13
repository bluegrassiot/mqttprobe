using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;

namespace MqttProbe.Core.Tests.Services.Configuration;

[TestFixture]
public class PreferenceSettingsTests
{
    private string _configPath = null!;
    private SettingsDocument _document = null!;
    private PreferenceSettings _preferences = null!;

    [SetUp]
    public void Setup()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"mqttprobe_settings_{Guid.NewGuid()}.json");
        if (File.Exists(_configPath)) File.Delete(_configPath);
        _document = new SettingsDocument(_configPath);
        _document.Config.Sparkplug ??= new SparkplugSettings();
        _preferences = new PreferenceSettings(_document);
    }

    [TearDown]
    public void TearDown()
    {
        _document.Dispose();
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Test]
    public async Task SetThemeAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _preferences.UiPreferencesChanged += () => fired = true;

        await _preferences.SetThemeAsync("light");

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetFontProfileAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _preferences.UiPreferencesChanged += () => fired = true;

        await _preferences.SetFontProfileAsync(FontProfiles.Accessible);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetFontProfileAsync_NormalizesUnknownToStandard()
    {
        await _preferences.SetFontProfileAsync("garbage");

        _document.Config.Ui.FontProfile.Should().Be(FontProfiles.Standard);
    }

    [Test]
    public async Task SetFontProfileAsync_PersistsValue()
    {
        await _preferences.SetFontProfileAsync(FontProfiles.Accessible);

        _document.Config.Ui.FontProfile.Should().Be(FontProfiles.Accessible);

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();
        reloaded.Config.Ui.FontProfile.Should().Be(FontProfiles.Accessible);
    }

    [Test]
    public async Task SetAutoResubscribeAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _preferences.UiPreferencesChanged += () => fired = true;

        await _preferences.SetAutoResubscribeAsync(false);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetAutoRequestRebirthAsync_PersistsAndRaisesSparkplugSettingsChanged()
    {
        var fired = false;
        _preferences.SparkplugSettingsChanged += () => fired = true;
        _document.Config.Sparkplug.Should().NotBeNull();
        _document.Config.Sparkplug!.AutoRequestRebirth.Should().BeFalse("auto-rebirth ships off");

        await _preferences.SetAutoRequestRebirthAsync(true);

        fired.Should().BeTrue();

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();
        reloaded.Config.Sparkplug!.AutoRequestRebirth.Should().BeTrue();
    }

    [Test]
    public async Task SetAllowNodeRebootAsync_PersistsAndRaisesSparkplugSettingsChanged()
    {
        var fired = false;
        _preferences.SparkplugSettingsChanged += () => fired = true;
        _document.Config.Sparkplug.Should().NotBeNull();
        _document.Config.Sparkplug!.AllowNodeReboot.Should().BeFalse("allow-node-reboot ships off");

        await _preferences.SetAllowNodeRebootAsync(true);

        fired.Should().BeTrue();

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();
        reloaded.Config.Sparkplug!.AllowNodeReboot.Should().BeTrue();
    }

    [Test]
    public async Task SetEnrichAliasNamesAsync_PersistsAndRaisesSparkplugSettingsChanged()
    {
        var fired = false;
        _preferences.SparkplugSettingsChanged += () => fired = true;
        _document.Config.Sparkplug!.EnrichAliasNames.Should().BeTrue("enrich ships on");

        await _preferences.SetEnrichAliasNamesAsync(false);

        fired.Should().BeTrue();

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();
        reloaded.Config.Sparkplug!.EnrichAliasNames.Should().BeFalse();
    }

    [Test]
    public async Task SetRebirthCooldownSecondsAsync_PersistsInRangedValue()
    {
        await _preferences.SetRebirthCooldownSecondsAsync(60);

        _document.Config.Sparkplug!.RebirthCooldownSeconds.Should().Be(60);

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();
        reloaded.Config.Sparkplug!.RebirthCooldownSeconds.Should().Be(60);
    }

    [Test]
    public async Task SetRebirthCooldownSecondsAsync_ClampsBelowMinimum()
    {
        await _preferences.SetRebirthCooldownSecondsAsync(1);

        _document.Config.Sparkplug!.RebirthCooldownSeconds.Should().Be(5);
    }

    [Test]
    public async Task SetRebirthCooldownSecondsAsync_ClampsAboveMaximum()
    {
        await _preferences.SetRebirthCooldownSecondsAsync(999);

        _document.Config.Sparkplug!.RebirthCooldownSeconds.Should().Be(600);
    }

    [Test]
    public async Task SetRebirthCooldownSecondsAsync_RaisesSparkplugSettingsChanged()
    {
        var fired = false;
        _preferences.SparkplugSettingsChanged += () => fired = true;

        await _preferences.SetRebirthCooldownSecondsAsync(60);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SparkplugSetters_DoNotRaiseUiPreferencesChanged()
    {
        var fired = false;
        _preferences.UiPreferencesChanged += () => fired = true;

        await _preferences.SetAutoRequestRebirthAsync(true);
        await _preferences.SetAllowNodeRebootAsync(true);
        await _preferences.SetEnrichAliasNamesAsync(false);
        await _preferences.SetRebirthCooldownSecondsAsync(60);

        fired.Should().BeFalse("sparkplug setters must not raise the UI event");
    }

    [Test]
    public async Task DismissHintAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _preferences.UiPreferencesChanged += () => fired = true;

        await _preferences.DismissHintAsync("any-hint");

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetPasswordAsync_RoundTripsThroughVerifyCredentials()
    {
        await _preferences.SetPasswordAsync("admin", "correct-horse");

        _preferences.VerifyCredentials("admin", "correct-horse").Should().BeTrue();
        _preferences.VerifyCredentials("admin", "wrong").Should().BeFalse();
        _preferences.VerifyCredentials("someone-else", "correct-horse").Should().BeFalse();

        using var reloaded = new SettingsDocument(_configPath);
        await new SettingsLoader(reloaded, new ConnectionSecrets(null, null), false, null).LoadAsync();
        new PreferenceSettings(reloaded).VerifyCredentials("admin", "correct-horse").Should().BeTrue();
    }

    [Test]
    public async Task SetMaxStoredMessagesAsync_PersistsValue()
    {
        await _preferences.SetMaxStoredMessagesAsync(5000);

        _document.Config.Performance.MaxStoredMessages.Should().Be(5000);
    }

    [Test]
    public async Task SetMaxStoredMessagesAsync_RaisesPerformanceSettingsChanged()
    {
        var fired = false;
        _preferences.PerformanceSettingsChanged += () => fired = true;

        await _preferences.SetMaxStoredMessagesAsync(5000);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetMaxStoredMessagesAsync_DoesNotRaiseUiPreferencesChanged()
    {
        var fired = false;
        _preferences.UiPreferencesChanged += () => fired = true;

        await _preferences.SetMaxStoredMessagesAsync(5000);

        fired.Should().BeFalse("performance setters must not raise the UI event");
    }

    [Test]
    public async Task SetMaxMessagesPerSecondAsync_DoesNotRaiseUiPreferencesChanged()
    {
        var fired = false;
        _preferences.UiPreferencesChanged += () => fired = true;

        await _preferences.SetMaxMessagesPerSecondAsync(2000);

        fired.Should().BeFalse("performance setters must not raise the UI event");
    }

    [Test]
    public async Task SetMaxMessagesPerSecondAsync_PersistsValue()
    {
        await _preferences.SetMaxMessagesPerSecondAsync(2000);

        _document.Config.Performance.MaxMessagesPerSecond.Should().Be(2000);
    }

    [Test]
    public async Task SetMaxMessagesPerSecondAsync_RaisesPerformanceSettingsChanged()
    {
        var fired = false;
        _preferences.PerformanceSettingsChanged += () => fired = true;

        await _preferences.SetMaxMessagesPerSecondAsync(2000);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetMaxDisplayMessagesAsync_PersistsValue()
    {
        await _preferences.SetMaxDisplayMessagesAsync(300);

        _document.Config.Performance.MaxDisplayMessages.Should().Be(300);
    }

    [Test]
    public async Task SetMaxDisplayMessagesAsync_RaisesPerformanceSettingsChanged()
    {
        var fired = false;
        _preferences.PerformanceSettingsChanged += () => fired = true;

        await _preferences.SetMaxDisplayMessagesAsync(300);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetMaxDisplayMessagesAsync_DoesNotRaiseUiPreferencesChanged()
    {
        var fired = false;
        _preferences.UiPreferencesChanged += () => fired = true;

        await _preferences.SetMaxDisplayMessagesAsync(300);

        fired.Should().BeFalse("performance setters must not raise the UI event");
    }

    [Test]
    public async Task SetMaxTopicNodesAsync_Roundtrips()
    {
        await _preferences.SetMaxTopicNodesAsync(5000);

        _document.Config.Performance.MaxTopicNodes.Should().Be(5000);
    }

    [Test]
    public async Task SetMaxTopicNodesAsync_RejectsValuesBelow100()
    {
        await _preferences.SetMaxTopicNodesAsync(5000);
        await _preferences.SetMaxTopicNodesAsync(50);

        _document.Config.Performance.MaxTopicNodes.Should().Be(5000);
    }
}
