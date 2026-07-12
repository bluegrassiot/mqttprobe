using MqttProbe.Services.Configuration;

namespace MqttProbe.Tests;

[TestFixture]
public class ConfigMigratorTests
{
    private string _root = null!;
    private string LegacyDir => Path.Combine(_root, "legacy", "config");
    private string NewDir => Path.Combine(_root, "new", "config");

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("mqttprobe-migrate-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void MigrateIfNeeded_CopiesLegacyConfig_WhenNewDirMissing()
    {
        Directory.CreateDirectory(LegacyDir);
        File.WriteAllText(Path.Combine(LegacyDir, "appsettings.json"), "{\"a\":1}");

        var migrated = ConfigMigrator.MigrateIfNeeded(LegacyDir, NewDir);

        migrated.Should().BeTrue();
        File.ReadAllText(Path.Combine(NewDir, "appsettings.json")).Should().Be("{\"a\":1}");
        // legacy left in place as backup
        File.Exists(Path.Combine(LegacyDir, "appsettings.json")).Should().BeTrue();
    }

    [Test]
    public void MigrateIfNeeded_CopiesNestedFiles()
    {
        Directory.CreateDirectory(Path.Combine(LegacyDir, "sub"));
        File.WriteAllText(Path.Combine(LegacyDir, "sub", "charts.json"), "[]");

        ConfigMigrator.MigrateIfNeeded(LegacyDir, NewDir).Should().BeTrue();

        File.Exists(Path.Combine(NewDir, "sub", "charts.json")).Should().BeTrue();
    }

    [Test]
    public void MigrateIfNeeded_DoesNothing_WhenNewDirAlreadyExists()
    {
        Directory.CreateDirectory(LegacyDir);
        File.WriteAllText(Path.Combine(LegacyDir, "appsettings.json"), "old");
        Directory.CreateDirectory(NewDir);
        File.WriteAllText(Path.Combine(NewDir, "appsettings.json"), "current");

        ConfigMigrator.MigrateIfNeeded(LegacyDir, NewDir).Should().BeFalse();

        File.ReadAllText(Path.Combine(NewDir, "appsettings.json")).Should().Be("current");
    }

    [Test]
    public void MigrateIfNeeded_DoesNothing_WhenLegacyDirMissing()
    {
        ConfigMigrator.MigrateIfNeeded(LegacyDir, NewDir).Should().BeFalse();
        Directory.Exists(NewDir).Should().BeFalse();
    }
}
