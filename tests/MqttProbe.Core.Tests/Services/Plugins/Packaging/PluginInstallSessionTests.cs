using MqttProbe.Core.Services.Plugins.Packaging;

namespace MqttProbe.Core.Tests.Services.Plugins.Packaging;

[TestFixture]
public class PluginInstallSessionTests
{
    [Test]
    public void TryGet_UnknownId_ReturnsFalse()
    {
        var session = new PluginInstallSession();

        session.TryGet("unknown", out var requiresRestart).Should().BeFalse();
        requiresRestart.Should().BeFalse();
    }

    [Test]
    public void Record_ThenTryGet_ReturnsTheRecordedValue()
    {
        var session = new PluginInstallSession();

        session.Record("demo", requiresRestart: true);

        session.TryGet("demo", out var requiresRestart).Should().BeTrue();
        requiresRestart.Should().BeTrue();
    }

    [Test]
    public void Record_False_After_Record_True_Keeps_RequiresRestart_True()
    {
        var session = new PluginInstallSession();

        session.Record("demo", requiresRestart: true);
        session.Record("demo", requiresRestart: false);

        session.TryGet("demo", out var requiresRestart).Should().BeTrue();
        requiresRestart.Should().BeTrue("a later no-restart install must not erase an earlier pending restart");
        session.RequiresRestart.Should().BeTrue();
    }

    [Test]
    public void HasPending_IsFalse_Until_Something_Is_Recorded()
    {
        var session = new PluginInstallSession();

        session.HasPending.Should().BeFalse();

        session.Record("demo", requiresRestart: false);

        session.HasPending.Should().BeTrue();
    }

    [Test]
    public void RequiresRestart_IsTrue_When_Any_Pending_Entry_Requires_It()
    {
        var session = new PluginInstallSession();

        session.Record("no-restart-needed", requiresRestart: false);
        session.RequiresRestart.Should().BeFalse();

        session.Record("restart-needed", requiresRestart: true);
        session.RequiresRestart.Should().BeTrue();
    }

    [Test]
    public void Clear_RemovesAllPendingEntries()
    {
        var session = new PluginInstallSession();

        session.Record("demo", requiresRestart: true);
        session.Clear();

        session.HasPending.Should().BeFalse();
        session.RequiresRestart.Should().BeFalse();
        session.TryGet("demo", out _).Should().BeFalse();
    }

    [Test]
    public void CanApplyWithoutRestart_IsTrue_When_Any_Pending_Entry_Does_Not_Require_It()
    {
        var session = new PluginInstallSession();

        session.Record("restart-needed", requiresRestart: true);
        session.CanApplyWithoutRestart.Should().BeFalse();

        session.Record("no-restart-needed", requiresRestart: false);
        session.CanApplyWithoutRestart.Should().BeTrue(
            "a schema change can activate without a restart even while an assembly change awaits one");
    }

    [Test]
    public void ClearNonRestartEntries_RemovesOnlyEntriesThatDoNotRequireRestart()
    {
        var session = new PluginInstallSession();

        session.Record("restart-needed", requiresRestart: true);
        session.Record("no-restart-needed", requiresRestart: false);

        session.ClearNonRestartEntries();

        session.TryGet("no-restart-needed", out _).Should().BeFalse();
        session.TryGet("restart-needed", out var requiresRestart).Should().BeTrue(
            "an entry still awaiting a restart has nothing to apply yet and must stay recorded");
        requiresRestart.Should().BeTrue();
    }

    [Test]
    public void Record_IsCaseInsensitive_By_Id()
    {
        var session = new PluginInstallSession();

        session.Record("Demo", requiresRestart: true);

        session.TryGet("demo", out var requiresRestart).Should().BeTrue();
        requiresRestart.Should().BeTrue();
    }
}
