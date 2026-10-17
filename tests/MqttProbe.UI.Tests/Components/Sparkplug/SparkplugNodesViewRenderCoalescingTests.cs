using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesViewRenderCoalescingTests : BunitTestContext
{
    private const int BurstSize = 500;

    private const int DormantRenderWindowMs = 600_000;

    private ISparkplugTopologyService _mockTopology = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockTopology = Substitute.For<ISparkplugTopologyService>();
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>());
        Services.AddSingleton(_mockTopology);
        Services.AddSingleton(Substitute.For<ISparkplugCommandService>());

        var cfg = new AppConfiguration();
        var settings = Substitute.For<ISparkplugSettings>();
        settings.Sparkplug.Returns(cfg.Sparkplug ?? new SparkplugSettings());
        Services.AddSparkplugSettings(settings);

        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());
        EnsureMudProviders();
    }

    [Test]
    public void TopologyChanged_Burst_DoesNotRenderOncePerEvent()
    {
        var cut = Render<SparkplugNodesView>();
        var coalescer = cut.Instance.RenderCoalescerForTest;
        coalescer.WindowMs = DormantRenderWindowMs;
        var rendersBefore = cut.RenderCount;

        RaiseTopologyBurst(BurstSize);

        cut.RenderCount.Should().BeLessThanOrEqualTo(rendersBefore + 1,
            "a 500-event burst queues at most one refresh, not one render per event");
        coalescer.QueuedCount.Should().Be(0, "the refresh waits for the coalesce window to elapse");
        coalescer.IsPending.Should().BeTrue("the burst claimed the single pending slot");
    }

    [Test]
    public void TopologyChanged_Burst_RendersLatestTopologyOnce()
    {
        var cut = Render<SparkplugNodesView>();
        var coalescer = cut.Instance.RenderCoalescerForTest;
        coalescer.WindowMs = DormantRenderWindowMs;

        PublishSingleNode("factory", "edge-01");
        RaiseTopologyBurst(BurstSize);

        coalescer.Tick();
        coalescer.Tick();

        coalescer.ExecutedCount.Should().Be(1, "one refresh serves the whole burst");
        coalescer.QueuedCount.Should().Be(1, "the consumed slot cannot be replayed");
        coalescer.IsPending.Should().BeFalse();
        cut.Markup.Should().Contain("edge-01");
    }

    [Test]
    public void TopologyChanged_SingleEvent_RefreshesWithoutManualFlush()
    {
        var cut = Render<SparkplugNodesView>();

        PublishSingleNode("factory", "edge-01");
        RaiseTopologyBurst(1);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("edge-01"), TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task BlockedDispatcher_BurstAndRepeatedTicks_QueuesOneRefresh()
    {
        var cut = Render<SparkplugNodesView>();
        var coalescer = cut.Instance.RenderCoalescerForTest;
        coalescer.WindowMs = DormantRenderWindowMs;
        var blocked = new BlockedRenderDispatcher { Hold = true };
        coalescer.DispatchOverrideForTest = blocked.Dispatch;

        PublishSingleNode("factory", "edge-01");
        for (var window = 0; window < 4; window++)
        {
            RaiseTopologyBurst(BurstSize);
            coalescer.Tick();
        }

        blocked.PostedCount.Should().Be(1, "the claimed slot blocks every later refresh while the renderer is stuck");
        coalescer.ExecutedCount.Should().Be(0);
        coalescer.IsPending.Should().BeTrue();

        PublishSingleNode("factory", "edge-02");
        await blocked.ReleaseAsync();

        coalescer.ExecutedCount.Should().Be(1, "one refresh serves the whole backlog");
        coalescer.IsPending.Should().BeFalse();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("edge-02", "the refresh reads the topology as it stands when it runs");
            cut.Markup.Should().NotContain("edge-01");
        }, TimeSpan.FromSeconds(5));

        blocked.Hold = false;
        RaiseTopologyBurst(1);
        coalescer.Tick();

        blocked.PostedCount.Should().Be(2, "the coalescer is ready for the next window");
        coalescer.ExecutedCount.Should().Be(2);
    }

    [Test]
    public async Task DisposeAsync_AfterDispatchQueued_SkipsTheQueuedRefresh()
    {
        var cut = Render<SparkplugNodesView>();
        var coalescer = cut.Instance.RenderCoalescerForTest;
        coalescer.WindowMs = DormantRenderWindowMs;
        var blocked = new BlockedRenderDispatcher { Hold = true };
        coalescer.DispatchOverrideForTest = blocked.Dispatch;

        PublishSingleNode("factory", "edge-01");
        RaiseTopologyBurst(BurstSize);
        coalescer.Tick();
        blocked.PostedCount.Should().Be(1);

        await cut.Instance.DisposeAsync();
        await blocked.ReleaseAsync();

        coalescer.ExecutedCount.Should().Be(0, "a refresh queued before disposal must not run");
    }

    [Test]
    public void Tick_WhenDispatchThrows_ReleasesThePendingSlot()
    {
        var cut = Render<SparkplugNodesView>();
        var coalescer = cut.Instance.RenderCoalescerForTest;
        coalescer.WindowMs = DormantRenderWindowMs;
        var blocked = new BlockedRenderDispatcher { ThrowOnDispatch = true };
        coalescer.DispatchOverrideForTest = blocked.Dispatch;

        RaiseTopologyBurst(1);
        coalescer.Tick();

        coalescer.IsPending.Should().BeFalse("a dispatch that never reached the renderer must not hold the slot");

        blocked.ThrowOnDispatch = false;
        RaiseTopologyBurst(1);
        coalescer.Tick();

        coalescer.ExecutedCount.Should().Be(1, "the next notification still gets its refresh");
    }

    [Test]
    public void RelativeTimeTick_RefreshesThroughTheSameCoalescer()
    {
        var cut = Render<SparkplugNodesView>();
        var coalescer = cut.Instance.RenderCoalescerForTest;
        coalescer.WindowMs = DormantRenderWindowMs;
        var rendersBefore = cut.RenderCount;

        cut.Instance.OnRelativeTimeTick();

        cut.RenderCount.Should().Be(rendersBefore, "the timestamp tick shares the coalesced refresh path");
        coalescer.QueuedCount.Should().Be(0, "the refresh waits for the coalesce window");
        coalescer.IsPending.Should().BeTrue();

        coalescer.Tick();

        coalescer.ExecutedCount.Should().Be(1, "a quiet feed still refreshes so relative timestamps stay fresh");
        cut.RenderCount.Should().BeGreaterThan(rendersBefore);
    }

    [Test]
    public void TopologyChanged_DefersStateReconciliationToTheDispatchedRefresh()
    {
        var groupA = new SpbGroup();
        groupA.Nodes["edge-A"] = new SpbNode { NodeId = "edge-A", GroupId = "alpha", Status = SpbNodeStatus.Online };
        var groupB = new SpbGroup();
        groupB.Nodes["edge-B"] = new SpbNode { NodeId = "edge-B", GroupId = "beta", Status = SpbNodeStatus.Online };
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>
        {
            ["alpha"] = groupA,
            ["beta"] = groupB,
        });

        var cut = Render<SparkplugNodesView>();
        var coalescer = cut.Instance.RenderCoalescerForTest;
        coalescer.WindowMs = DormantRenderWindowMs;
        cut.Instance.SetGroupFilterForTest("alpha");

        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["beta"] = groupB });
        _mockTopology.TopologyChanged += Raise.Event<Action>();

        cut.Instance.GetGroupFilterForTest().Should().Be("alpha",
            "the broker thread must not touch the state the render reads");

        coalescer.Tick();

        cut.Instance.GetGroupFilterForTest().Should().BeNull("the dispatched refresh reconciles the filter");
    }

    [Test]
    public async Task DisposeAsync_ThenTopologyEvents_DoesNotThrow()
    {
        var cut = Render<SparkplugNodesView>();
        await cut.Instance.DisposeAsync();

        var act = () =>
        {
            PublishSingleNode("factory", "edge-01");
            RaiseTopologyBurst(BurstSize);
        };

        act.Should().NotThrow();
        cut.Instance.RenderCoalescerForTest.QueuedCount.Should().Be(0, "a disposed view queues nothing");
    }

    [Test]
    public async Task DisposeAsync_WithPendingRefresh_QueuesNothing()
    {
        var cut = Render<SparkplugNodesView>();
        var coalescer = cut.Instance.RenderCoalescerForTest;
        coalescer.WindowMs = DormantRenderWindowMs;
        RaiseTopologyBurst(BurstSize);
        coalescer.IsPending.Should().BeTrue();

        await cut.Instance.DisposeAsync();

        coalescer.QueuedCount.Should().Be(0, "the pending refresh never reached the renderer");
        coalescer.ExecutedCount.Should().Be(0, "a disposed view renders nothing");
    }

    private void RaiseTopologyBurst(int count)
    {
        for (var i = 0; i < count; i++)
            _mockTopology.TopologyChanged += Raise.Event<Action>();
    }

    private void PublishSingleNode(string groupId, string nodeId)
    {
        var group = new SpbGroup();
        group.Nodes[nodeId] = new SpbNode { NodeId = nodeId, GroupId = groupId, Status = SpbNodeStatus.Online };
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { [groupId] = group });
    }
}
