using AngleSharp.Dom;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicBrowserMetricSelectionTests : BunitTestContext
{
    private const string NodeTopic = "spBv1.0/plant1/NDATA/node1";
    private const string DeviceTopic = "spBv1.0/plant1/DDATA/node1/dev1";
    private const string MotorTemp = "Motor/Temp";

    private static readonly SparkplugSelectionScope _nodeScope =
        new(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1");

    private static readonly SparkplugSelectionScope _deviceScope =
        new(SparkplugSelectionKind.Device, "spBv1.0/plant1", "node1", "dev1");

    private TopicBrowserTestFixture _fixture = null!;
    private ISparkplugTopologyService _topology = null!;
    private SpbNode _node = null!;
    private readonly UiPreferences _preferences = new();
    private IUiSettings _uiSettings = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = new TopicBrowserTestFixture();
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);

        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _preferences.TopicTreeMultiSelect = false;
        _uiSettings = SettingsSubstituteRegistration.CreateUiSettings(_preferences);
        Services.AddUiSettings(_uiSettings);
        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());
        Services.AddSingleton(Substitute.For<ITopicExcludeService>());
        Services.AddSingleton(Substitute.For<IMessageStoreManager>());

        _node = new SpbNode { NodeId = "node1", GroupId = "plant1", Status = SpbNodeStatus.Online };
        _node.Metrics = [Metric(MotorTemp, "21.5"), Metric("Temp", "3")];
        var device = new SpbDevice { DeviceId = "dev1", NodeId = "node1", GroupId = "plant1" };
        device.Metrics = [Metric("Speed/Value", "7")];
        _node.Devices["dev1"] = device;

        var group = new SpbGroup { GroupId = "plant1" };
        group.Nodes["node1"] = _node;
        _topology = Substitute.For<ISparkplugTopologyService>();
        _topology.Groups.Returns(new Dictionary<string, SpbGroup>(StringComparer.Ordinal)
        {
            ["plant1"] = group
        });

        AddNodeMessage("alias-only", MotorTemp);
        AddNodeMessage("temperature", "Temp");
        _fixture.Adapter.Add(DeviceTopic, new MqttMessage("speed", DeviceTopic, false,
            MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = DeviceTopic,
            SparkplugMetricNames = ["Speed/Value"]
        });
    }

    private static SpbMetricSnapshot Metric(string name, string value) =>
        new(name, "double", value, DateTime.UnixEpoch);

    private void AddNodeMessage(string payload, params string[] names) =>
        _fixture.Adapter.Add(NodeTopic, new MqttMessage(payload, NodeTopic, false,
            MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = NodeTopic,
            SparkplugMetricNames = [.. names]
        });

    private IRenderedComponent<TopicBrowser> RenderExpanded()
    {
        var cut = Render<TopicBrowser>(parameters => parameters
            .Add(component => component.TopologyService, _topology));
        EnsureMudProviders();
        cut.Find("[aria-label='Expand all']").Click();
        return cut;
    }

    private static IElement MetricRow(IRenderedComponent<TopicBrowser> cut, string name)
    {
        var rows = cut.FindAll($"[data-metric-name='{name}']");
        return rows.Single();
    }

    private static IElement FolderRow(IRenderedComponent<TopicBrowser> cut, string label) =>
        cut.FindAll("[data-metric-folder='true']")
            .Single(row => row.QuerySelector(".topic-name")?.TextContent.Trim() == label);

    private static void ExpandFirstMetricsGroup(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll("[data-metric-group='true']").First().Click();

    [Test]
    public void ClickingAMetricLeaf_SelectsTheExactOwnerAndMetric()
    {
        var cut = RenderExpanded();

        MetricRow(cut, MotorTemp).Click();

        var request = _fixture.Adapter.Selections.Single()!;
        request.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        request.Metric.Should().Be(new MqttSparkplugMetricTarget(_nodeScope, MotorTemp));
        request.Topics.Should().BeEmpty();
        request.Branches.Should().BeEmpty();

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Label.Should().Be("spBv1.0/plant1/node1 Motor/Temp");
        state.MessageCount.Should().Be(1, "the alias-only update carries the metric name");
        state.Paths.Should().BeEmpty();
        state.Branches.Should().BeEmpty();
        MetricRow(cut, MotorTemp).ClassList.Should().Contain("topic-tree-row--selected");
    }

    [Test]
    public void ClickingAMetricFolder_OnlyExpandsIt()
    {
        _node.Metrics = [Metric("Pure/Leaf", "1")];
        var cut = RenderExpanded();

        FolderRow(cut, "Pure").Click();

        _fixture.Adapter.Selections.Should().BeEmpty("a pure folder is not a selectable metric");
        cut.WaitForAssertion(() => cut.FindAll("[data-metric-name='Pure/Leaf']").Should().BeEmpty());
    }

    [Test]
    public void AValueThatAlsoHasChildren_SelectsOnClickAndExpandsOnChevron()
    {
        _node.Metrics = [Metric("A", "1"), Metric("A/B", "2")];
        var cut = RenderExpanded();

        var a = MetricRow(cut, "A");
        a.QuerySelector(".topic-chevron").Should().NotBeNull("A also owns the nested metric A/B");
        a.Click();
        _fixture.Adapter.Store.GetSelectedTopicState().Metric!.MetricName.Should().Be("A");

        MetricRow(cut, "A").QuerySelector(".topic-chevron")!.Click();

        _fixture.Adapter.Store.GetSelectedTopicState().Metric!.MetricName.Should().Be("A",
            "the chevron toggles expansion without changing the selected metric");
        cut.WaitForAssertion(() => cut.FindAll("[data-metric-name='A/B']").Should().BeEmpty());
        MetricRow(cut, "A").QuerySelector(".topic-chevron")!.Click();
        cut.WaitForAssertion(() => MetricRow(cut, "A/B").Should().NotBeNull());
        MetricRow(cut, "A/B").Click();
        _fixture.Adapter.Store.GetSelectedTopicState().Metric!.MetricName.Should().Be("A/B",
            "both A and A/B keep their own exact selection");
    }

    [Test]
    public void KeyboardActivation_ExpandsAPureMetricFolderWithoutSelecting()
    {
        _node.Metrics = [Metric("Pure/Leaf", "1")];
        var cut = RenderExpanded();

        FolderRow(cut, "Pure").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        _fixture.Adapter.Selections.Should().BeEmpty();
        cut.WaitForAssertion(() => cut.FindAll("[data-metric-name='Pure/Leaf']").Should().BeEmpty());
    }

    [TestCase("Enter")]
    [TestCase(" ")]
    public void KeyboardActivation_SelectsTheMetric(string key)
    {
        var cut = RenderExpanded();

        MetricRow(cut, MotorTemp).KeyDownAsync(new KeyboardEventArgs { Key = key });

        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Metric.Should()
                .Be(new MqttSparkplugMetricTarget(_nodeScope, MotorTemp)));
    }

    [Test]
    public void CollapseAndSearch_KeepTheSelectedMetric()
    {
        var cut = RenderExpanded();
        MetricRow(cut, MotorTemp).Click();

        cut.Find("[aria-label='Collapse all']").Click();
        _fixture.Adapter.Store.GetSelectedTopicState().Metric.Should()
            .Be(new MqttSparkplugMetricTarget(_nodeScope, MotorTemp));

        cut.Find(".topic-filter-field input").Input("no-such-row");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No topics match"));

        _fixture.Adapter.Store.GetSelectedTopicState().Metric.Should()
            .Be(new MqttSparkplugMetricTarget(_nodeScope, MotorTemp));
        _fixture.Adapter.ConditionalClearCalls.Should().Be(0);
    }

    private static void ExpandAll(IRenderedComponent<TopicBrowser> cut) =>
        cut.Find("[aria-label='Expand all']").Click();

    private static Task RefreshOnNextTickAsync(IRenderedComponent<TopicBrowser> cut) =>
        cut.InvokeAsync(() => cut.Instance.RefreshAsync());

    [Test]
    public async Task OfflineOwnerAndZeroHistory_KeepTheSelectedMetricValue()
    {
        var cut = RenderExpanded();
        MetricRow(cut, "Temp").Click();

        _node.Status = SpbNodeStatus.Offline;
        _node.Metrics = [Metric("Temp", "3")];
        _fixture.Adapter.Store.RemoveMatchingTopic(NodeTopic);
        AddNodeMessage("no metric name");

        _topology.TopologyChanged += Raise.Event<Action>();
        await RefreshOnNextTickAsync(cut);

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Metric!.MetricName.Should().Be("Temp");
        state.MessageCount.Should().Be(0, "the owner row remains while no message names the metric");
        MetricRow(cut, "Temp").QuerySelector(".topic-metric-value")!.TextContent.Should().Be("3",
            "the last value is kept while the node is offline");
    }

    [Test]
    public void TurningGroupingOff_ClearsTheMetricSelection()
    {
        var cut = RenderExpanded();
        MetricRow(cut, MotorTemp).Click();

        _preferences.GroupSparkplugTopicsByNodeAndDevice = false;
        _uiSettings.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull());
        cut.Markup.Should().NotContain("Grouped by node and device");
    }

    [Test]
    public void TurningMultiSelectOn_KeepsTheSelectedMetricWithoutBranchChecks()
    {
        var cut = RenderExpanded();
        MetricRow(cut, MotorTemp).Click();

        _preferences.TopicTreeMultiSelect = true;
        _uiSettings.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
            state.Metric.Should().Be(new MqttSparkplugMetricTarget(_nodeScope, MotorTemp));

            MetricRow(cut, MotorTemp).ClassList.Should().Contain("topic-tree-row--selected");
            MetricRow(cut, MotorTemp).QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull();
            AssertNoTopicCheckboxesChecked(cut);
            cut.FindAll("[data-metric-group='true']").Should().NotBeEmpty();
        });
    }

    private IRenderedComponent<TopicBrowser> RenderMultiSelectExpanded()
    {
        _preferences.TopicTreeMultiSelect = true;
        return RenderExpanded();
    }

    private static IElement BranchRow(IRenderedComponent<TopicBrowser> cut, string path) =>
        cut.FindAll(".topic-tree-row")
            .Single(row => row.GetAttribute("data-topic-path") == path);

    private static void ToggleBranchCheckbox(IRenderedComponent<TopicBrowser> cut, string path) =>
        BranchRow(cut, path).QuerySelector(".topic-select-checkbox-wrap")!.Click();

    private static void ToggleMetricCheckbox(IElement row) =>
        row.QuerySelector(".topic-select-checkbox-wrap")!.Click();

    private static IElement MetricGroupRow(IRenderedComponent<TopicBrowser> cut, string owner) =>
        cut.FindAll("[data-metric-group='true']").Single(row =>
            row.QuerySelector(".topic-select-checkbox-wrap")?.GetAttribute("title")
            == $"Select all metrics for {owner}");

    private static IElement MetricRowFor(IRenderedComponent<TopicBrowser> cut, string name, string owner) =>
        cut.FindAll($"[data-metric-name='{name}']").Single(row =>
            row.QuerySelector(".topic-select-checkbox-wrap")?.GetAttribute("title")
                ?.EndsWith(owner, StringComparison.Ordinal) == true);

    private static void AssertNoTopicCheckboxesChecked(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".topic-tree-row")
            .Where(row => row.GetAttribute("data-metric-name") is null
                && row.GetAttribute("data-metric-folder") is null
                && row.GetAttribute("data-metric-group") is null)
            .SelectMany(row => row.QuerySelectorAll(".topic-select-checkbox input[checked]"))
            .Should().BeEmpty("only metric checkboxes may be checked");

    [Test]
    public void MultiSelect_MetricLeafClickClearsCheckedTopicsAndFiltersExactly()
    {
        var cut = RenderMultiSelectExpanded();
        ToggleBranchCheckbox(cut, NodeTopic);
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.Branches));

        MetricRow(cut, MotorTemp).Click();

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
            state.Metric.Should().Be(new MqttSparkplugMetricTarget(_nodeScope, MotorTemp));
            state.Branches.Should().BeEmpty("the checked branch cannot survive the metric filter");

            MetricRow(cut, MotorTemp).QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull();
            AssertNoTopicCheckboxesChecked(cut);
            MetricRow(cut, MotorTemp).ClassList.Should().Contain("topic-tree-row--selected");
        });
    }

    [Test]
    public void MultiSelect_CheckingATopicBranchReplacesTheMetricFilter()
    {
        var cut = RenderMultiSelectExpanded();
        MetricRow(cut, MotorTemp).Click();
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.SparkplugMetric));

        ToggleBranchCheckbox(cut, NodeTopic);

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Mode.Should().Be(TopicSelectionMode.Branches);
            state.Metric.Should().BeNull();
            state.Branches.Should().ContainSingle().Which.Path.Should().Be(NodeTopic);

            BranchRow(cut, NodeTopic).QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull();
            MetricRow(cut, MotorTemp).QuerySelector(".topic-select-checkbox input[checked]")
                .Should().BeNull();
            MetricRow(cut, MotorTemp).ClassList.Should().NotContain("topic-tree-row--selected");
        });
    }

    [Test]
    public void MultiSelect_MetricFolderCheckboxSelectsKnownDescendantsAndTriStates()
    {
        _node.Metrics = [Metric("Motor/A", "1"), Metric("Motor/B", "2"), Metric("Other", "3")];
        var cut = RenderMultiSelectExpanded();
        var folder = FolderRow(cut, "Motor");

        ToggleMetricCheckbox(folder);

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Metrics.Select(metric => metric.MetricName).Should().Equal("Motor/A", "Motor/B");
            FolderRow(cut, "Motor").QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull();
            MetricGroupRow(cut, "spBv1.0/plant1/node1")
                .QuerySelector(".topic-select-checkbox .mud-checkbox-null").Should().NotBeNull(
                    "the untouched sibling metric keeps the owner group mixed");
        });

        ToggleMetricCheckbox(MetricRow(cut, "Motor/B"));
        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics.Select(metric => metric.MetricName)
                .Should().Equal("Motor/A");
            FolderRow(cut, "Motor").QuerySelector(".topic-select-checkbox .mud-checkbox-null")
                .Should().NotBeNull();
        });

        ToggleMetricCheckbox(FolderRow(cut, "Motor"));
        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics.Select(metric => metric.MetricName)
                .Should().Equal("Motor/A", "Motor/B");
            FolderRow(cut, "Motor").QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull();
        });

        ToggleMetricCheckbox(MetricRow(cut, "Other"));
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics.Select(metric => metric.MetricName)
                .Should().BeEquivalentTo("Motor/A", "Motor/B", "Other"));

        ToggleMetricCheckbox(FolderRow(cut, "Motor"));
        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics.Select(metric => metric.MetricName)
                .Should().Equal("Other");
            FolderRow(cut, "Motor").QuerySelector(".topic-select-checkbox input[checked]")
                .Should().BeNull();
            FolderRow(cut, "Motor").QuerySelector(".topic-select-checkbox .mud-checkbox-null")
                .Should().BeNull();
        });
    }

    [Test]
    public void MultiSelect_ValueAndNestedMetricRowBodyAndCheckboxBothToggleOwnAndDescendants()
    {
        _node.Metrics = [Metric("A", "1"), Metric("A/B", "2")];
        var cut = RenderMultiSelectExpanded();

        MetricRow(cut, "A").Click();

        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics.Select(metric => metric.MetricName)
                .Should().Equal("A", "A/B");
            MetricRow(cut, "A").QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull();
            MetricRow(cut, "A/B").QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull();
        });

        ToggleMetricCheckbox(MetricRow(cut, "A"));
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics.Should().BeEmpty());

        ToggleMetricCheckbox(MetricRow(cut, "A"));
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics.Select(metric => metric.MetricName)
                .Should().Equal("A", "A/B"));

        MetricRow(cut, "A").Click();
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics.Should().BeEmpty());
    }

    [Test]
    public async Task MultiSelect_NewDescendantKeepsTheParentMixed()
    {
        _node.Metrics = [Metric("A/B", "1")];
        var cut = RenderMultiSelectExpanded();

        ToggleMetricCheckbox(MetricRow(cut, "A/B"));
        cut.WaitForAssertion(() =>
            FolderRow(cut, "A").QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull("A/B is the folder's only known metric"));

        _node.Metrics = [Metric("A/B", "1"), Metric("A/C", "2")];
        _topology.TopologyChanged += Raise.Event<Action>();
        await RefreshOnNextTickAsync(cut);

        cut.WaitForAssertion(() =>
        {
            FolderRow(cut, "A").QuerySelector(".topic-select-checkbox .mud-checkbox-null")
                .Should().NotBeNull("a newly discovered descendant arrives unchecked");
            MetricRow(cut, "A/C").QuerySelector(".topic-select-checkbox input[checked]")
                .Should().BeNull();
        });
    }

    [Test]
    public void MultiSelect_ExactMetricIdentityKeepsCaseSlashesAndEmptySegments()
    {
        string[] names = ["Temp", "temp", "A//B", "A/B/"];
        _node.Metrics = [.. names.Select((name, index) => Metric(name, index.ToString()))];
        var cut = RenderMultiSelectExpanded();

        ToggleMetricCheckbox(MetricGroupRow(cut, "spBv1.0/plant1/node1"));

        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics
                .Select(metric => metric.MetricName).Should().BeEquivalentTo(names);
            foreach (var name in names)
                MetricRow(cut, name).QuerySelector(".topic-select-checkbox input[checked]")
                    .Should().NotBeNull(name + " keeps its exact checkbox");
        });
    }

    [Test]
    public void MultiSelect_CrossOwnerMetricNamesStayDistinct()
    {
        _node.Metrics = [Metric("Temp", "1")];
        _node.Devices["dev1"].Metrics = [Metric("Temp", "2")];
        var cut = RenderMultiSelectExpanded();

        ToggleMetricCheckbox(MetricRowFor(cut, "Temp", "spBv1.0/plant1/node1"));
        ToggleMetricCheckbox(MetricRowFor(cut, "Temp", "spBv1.0/plant1/node1/dev1"));

        cut.WaitForAssertion(() =>
        {
            var metrics = _fixture.Adapter.Store.GetSelectedTopicState().Metrics;
            metrics.Should().HaveCount(2);
            metrics.Select(metric => metric.Scope).Should().BeEquivalentTo(
                [_nodeScope, _deviceScope]);
            MetricRowFor(cut, "Temp", "spBv1.0/plant1/node1")
                .QuerySelector(".topic-select-checkbox input[checked]").Should().NotBeNull();
            MetricRowFor(cut, "Temp", "spBv1.0/plant1/node1/dev1")
                .QuerySelector(".topic-select-checkbox input[checked]").Should().NotBeNull();
        });
    }

    [Test]
    public void PickerMetricChoice_ClearsSharedBranchesBeforeTheExternalCallback()
    {
        _preferences.TopicTreeMultiSelect = true;
        var host = new TopicBulkSelection(_fixture.Adapter);
        host.SetMultiSelect(true);
        var root = new TopicBranchTarget(TopicBranchKind.Raw, Path: "spBv1.0");
        host.SelectAll([root]);
        var token = _fixture.Adapter.Store.GetSelectedTopicState().Token;
        var modeAtCallback = TopicSelectionMode.SingleTopic;
        var checksAtCallback = -1;

        var cut = Render<TopicBrowser>(parameters => parameters
            .Add(component => component.TopologyService, _topology)
            .Add(component => component.ForceSingleSelect, true)
            .AddCascadingValue(host)
            .Add(component => component.OnSelectedChanged,
                EventCallback.Factory.Create<SelectedTopicState>(this, _ =>
                {
                    modeAtCallback = _fixture.Adapter.Store.GetSelectedTopicState().Mode;
                    checksAtCallback = host.Count;
                })));
        EnsureMudProviders();
        cut.Find("[aria-label='Expand all']").Click();
        var toolbar = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(host));
        toolbar.WaitForAssertion(() =>
            toolbar.FindAll(".topic-bulk-toolbar__clear-messages").Should().ContainSingle());

        _fixture.Adapter.Store.GetSelectedTopicState().Token.Should().Be(token,
            "opening the picker is read-only");
        host.Branches.Should().ContainSingle().Which.Should().Be(root);
        host.IsPassive.Should().BeFalse();

        MetricRow(cut, MotorTemp).Click();

        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.SparkplugMetric));
        host.Count.Should().Be(0);
        modeAtCallback.Should().Be(TopicSelectionMode.SparkplugMetric);
        checksAtCallback.Should().Be(0, "shared checks clear before the external callback starts");
        toolbar.WaitForAssertion(() => toolbar.FindAll(".topic-bulk-toolbar").Should().BeEmpty());
    }

    [Test]
    public void MultiSelect_MetricsAcrossGroupsAndOwnersUnite()
    {
        const string plant2Topic = "spBv1.0/plant2/NDATA/node1";
        _fixture.Adapter.Add(plant2Topic, new MqttMessage("p", plant2Topic, false,
            MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = plant2Topic,
            SparkplugMetricNames = ["Temp"]
        });
        var plant2Node = new SpbNode { NodeId = "node1", GroupId = "plant2", Status = SpbNodeStatus.Online };
        plant2Node.Metrics = [Metric("Temp", "4")];
        var plant2 = new SpbGroup { GroupId = "plant2" };
        plant2.Nodes["node1"] = plant2Node;
        var plant1 = _topology.Groups["plant1"];
        _topology.Groups.Returns(new Dictionary<string, SpbGroup>(StringComparer.Ordinal)
        {
            ["plant1"] = plant1,
            ["plant2"] = plant2
        });
        var cut = RenderMultiSelectExpanded();

        ToggleMetricCheckbox(MetricRowFor(cut, "Temp", "spBv1.0/plant1/node1"));
        ToggleMetricCheckbox(MetricRowFor(cut, "Temp", "spBv1.0/plant2/node1"));

        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics
                .Select(metric => $"{metric.Scope.GroupPath}|{metric.MetricName}")
                .Should().BeEquivalentTo("spBv1.0/plant1|Temp", "spBv1.0/plant2|Temp");
        });
    }

    [Test]
    public async Task ForceSingleSelect_ShowsNoCheckboxesAndChoosingOneReplacesTheSet()
    {
        _preferences.TopicTreeMultiSelect = true;
        var cut = Render<TopicBrowser>(parameters => parameters
            .Add(component => component.TopologyService, _topology)
            .Add(component => component.ForceSingleSelect, true));
        EnsureMudProviders();
        cut.Find("[aria-label='Expand all']").Click();

        var both = new[]
        {
            new MqttSparkplugMetricTarget(_nodeScope, MotorTemp),
            new MqttSparkplugMetricTarget(_nodeScope, "Temp")
        };
        _fixture.Adapter.Store.SelectTopics(TopicSelection.ForSparkplugMetrics(both));
        await RefreshOnNextTickAsync(cut);

        cut.FindAll(".topic-select-checkbox").Should().BeEmpty();
        cut.FindAll(".topic-select-all").Should().BeEmpty();
        MetricRow(cut, MotorTemp).ClassList.Should().Contain("topic-tree-row--selected");
        MetricRow(cut, "Temp").ClassList.Should().Contain("topic-tree-row--selected",
            "single mode tolerates every selected metric until the user chooses one");

        MetricRow(cut, "Temp").Click();

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Metrics.Should().ContainSingle().Which.MetricName.Should().Be("Temp");
            cut.FindAll(".topic-select-checkbox").Should().BeEmpty();
        });
    }

    [Test]
    public void ForceSingleSelect_LeavesTheSharedBulkHostAndItsChecksAlone()
    {
        _preferences.TopicTreeMultiSelect = true;
        var host = new TopicBulkSelection(_fixture.Adapter);
        host.SetMultiSelect(true);
        var root = new TopicBranchTarget(TopicBranchKind.Raw, Path: "spBv1.0");
        host.SelectAll([root]);

        var cut = Render<TopicBrowser>(parameters => parameters
            .Add(component => component.TopologyService, _topology)
            .Add(component => component.ForceSingleSelect, true)
            .AddCascadingValue(host));
        EnsureMudProviders();
        cut.Find("[aria-label='Expand all']").Click();

        cut.FindAll(".topic-select-checkbox").Should().BeEmpty();
        host.IsPassive.Should().BeFalse("the shared host keeps its multi-select mode");
        host.Branches.Should().ContainSingle().Which.Should().Be(root);
    }

    [Test]
    public void MetricCheckboxAfterExternalClearUsesTheFreshCoreMetrics()
    {
        var cut = RenderMultiSelectExpanded();
        ToggleMetricCheckbox(MetricRow(cut, MotorTemp));
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics.Should().ContainSingle());

        var filter = Render<MetricFilterBar>(parameters => parameters
            .Add(component => component.Label, _fixture.Adapter.Store.GetSelectedTopicState().Label)
            .Add(component => component.Metrics, _fixture.Adapter.Store.GetSelectedTopicState().Metrics)
            .Add(component => component.OnClear, EventCallback.Factory.Create(
                this, () => _fixture.Adapter.Store.SelectTopics(null))));
        filter.Find(".pb-metric-filter__clear").Click();

        ToggleMetricCheckbox(MetricRow(cut, "Temp"));

        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics
                .Select(metric => metric.MetricName).Should().Equal("Temp"));
        MetricRow(cut, MotorTemp).QuerySelector(".topic-select-checkbox input[checked]")
            .Should().BeNull("the cleared metric must not be restored from the cached tree state");
    }

    [Test]
    public void MultiSelect_LastMetricUncheckNotifiesAfterCoreCommitted()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        _preferences.TopicTreeMultiSelect = true;
        var cut = Render<TopicBrowser>(parameters => parameters
            .Add(component => component.TopologyService, _topology)
            .AddCascadingValue(selection));
        EnsureMudProviders();
        cut.Find("[aria-label='Expand all']").Click();

        var observed = new List<(string? Key, int Metrics, int Checks)>();
        selection.Changed += () =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            observed.Add((state.Key, state.Metrics.Length, selection.Count));
        };

        ToggleMetricCheckbox(MetricRow(cut, MotorTemp));
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Metrics.Should().ContainSingle());
        ToggleMetricCheckbox(MetricRow(cut, MotorTemp));

        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull());
        var last = observed[^1];
        last.Key.Should().BeNull("observers see Core already cleared");
        last.Metrics.Should().Be(0);
        last.Checks.Should().Be(0);
    }

    [Test]
    public async Task SharedSelection_BranchToMetric_DropsDestructiveTargetsAndSelectsOnlyRealBranches()
    {
        _preferences.TopicTreeMultiSelect = true;
        var selection = new TopicBulkSelection(_fixture.Adapter);
        var cut = Render<TopicBrowser>(parameters => parameters
            .Add(component => component.TopologyService, _topology)
            .AddCascadingValue(selection));
        EnsureMudProviders();
        cut.Find("[aria-label='Expand all']").Click();
        var toolbar = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        ToggleBranchCheckbox(cut, NodeTopic);
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.Branches));
        toolbar.WaitForAssertion(() =>
            toolbar.FindAll(".topic-bulk-toolbar__clear-messages").Should().ContainSingle());

        var observed = new List<(TopicSelectionMode Mode, int Checks)>();
        selection.Changed += () => observed.Add(
            (_fixture.Adapter.Store.GetSelectedTopicState().Mode, selection.Count));

        MetricRow(cut, MotorTemp).Click();

        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.SparkplugMetric));
        toolbar.WaitForAssertion(() =>
            toolbar.FindAll(".topic-bulk-toolbar").Should().BeEmpty(
                "no checked targets remain, so no destructive action is offered"));
        observed.Should().ContainSingle().Which.Should()
            .Be((TopicSelectionMode.SparkplugMetric, 0));

        await RefreshOnNextTickAsync(cut);
        selection.Branches.Should().BeEmpty();
        _fixture.Adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.SparkplugMetric);

        var global = cut.Find(".topic-select-all input");
        await global.ChangeAsync(!global.HasAttribute("checked"));

        cut.WaitForAssertion(() =>
        {
            selection.Branches.Should().OnlyContain(branch =>
                branch.Kind == TopicBranchKind.Raw
                && branch.Scope == null
                && branch.MessageType == null);
            var roots = _fixture.Adapter.Store.GetTopicTreeSnapshot().Roots;
            selection.Branches.Select(branch => branch.Path)
                .Should().OnlyContain(path => roots.Any(root => root.FullTopic == path));
            _fixture.Adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.Branches);
        });
    }

    [Test]
    public void MultiSelect_MetricRowsAndTheirContainersCarryMetricCheckboxes()
    {
        var cut = RenderMultiSelectExpanded();

        var groups = cut.FindAll("[data-metric-group='true']").ToList();
        var folders = cut.FindAll("[data-metric-folder='true']").ToList();
        var values = cut.FindAll("[data-metric-name]").ToList();
        groups.Should().NotBeEmpty();
        folders.Should().NotBeEmpty();
        values.Should().NotBeEmpty();
        groups.Should().OnlyContain(row => row.QuerySelector(".topic-select-checkbox") != null);
        folders.Should().OnlyContain(row => row.QuerySelector(".topic-select-checkbox") != null);
        values.Should().OnlyContain(row => row.QuerySelector(".topic-select-checkbox") != null);
        groups.Concat(folders).Concat(values).Should()
            .OnlyContain(row => row.QuerySelector(".topic-select-checkbox-wrap") != null);
    }

    [Test]
    public void MultiSelect_MetricsGroupExpandsWithoutTouchingTheSelection()
    {
        var cut = RenderMultiSelectExpanded();

        var groupRow = cut.FindAll("[data-metric-group='true']")
            .Single(row => row.QuerySelector("[aria-label='Collapse Metrics for node1']") is not null);
        groupRow.Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-metric-name='Motor/Temp']").Should().BeEmpty());
        _fixture.Adapter.Selections.Should().BeEmpty("a group is a container, not a selection");
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
    }

    [Test]
    public async Task MultiSelect_TopologyRefreshKeepsTheMetricAndItsRowIdentity()
    {
        var cut = RenderMultiSelectExpanded();
        MetricRow(cut, MotorTemp).Click();
        var key = MetricRow(cut, MotorTemp).GetAttribute("data-topic-key");

        _node.Metrics = [Metric(MotorTemp, "22.5"), Metric("Temp", "3")];
        _topology.TopologyChanged += Raise.Event<Action>();
        await RefreshOnNextTickAsync(cut);

        cut.WaitForAssertion(() =>
        {
            MetricRow(cut, MotorTemp).GetAttribute("data-topic-key").Should().Be(key,
                "a metric value update must not move the virtualized row");
            MetricRow(cut, MotorTemp).QuerySelector(".topic-metric-value")!.TextContent.Trim()
                .Should().Be("22.5");
            _fixture.Adapter.Store.GetSelectedTopicState().Metric!.MetricName.Should().Be(MotorTemp);
            MetricRow(cut, MotorTemp).QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull();
            AssertNoTopicCheckboxesChecked(cut);
        });
    }

    [Test]
    public void MetricsGroupNamesItsExpandActionAndShowsItsMetricCount()
    {
        var cut = RenderMultiSelectExpanded();

        var groupRow = cut.FindAll("[data-metric-group='true']")
            .Single(row => row.QuerySelector("[aria-label='Collapse Metrics for node1']") is not null);

        groupRow.QuerySelector("[aria-label='Collapse Metrics for node1']").Should().NotBeNull();
        groupRow.GetAttribute("role").Should().Be("treeitem");
        groupRow.GetAttribute("aria-selected").Should().Be("false",
            "the group is expanded, never selected");
        groupRow.GetAttribute("aria-expanded").Should().Be("true");
        groupRow.QuerySelector(".topic-stat-text").Should().BeNull(
            "the group is not a topic, so it has no T and M totals");
        groupRow.QuerySelector(".topic-metric-count")!.TextContent.Trim().Should().Be("2");
        groupRow.GetAttribute("title").Should()
            .Be("Sparkplug metrics · 2 metrics · not a real MQTT topic");
    }

    [Test]
    public async Task BirthReplacementWithoutTheMetric_ClearsIt()
    {
        var cut = RenderExpanded();
        MetricRow(cut, MotorTemp).Click();

        _node.Metrics = [Metric("Temp", "3")];
        _topology.TopologyChanged += Raise.Event<Action>();
        await RefreshOnNextTickAsync(cut);

        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
    }

    [Test]
    public async Task PurgedOwner_ClearsTheMetric()
    {
        var cut = RenderExpanded();
        MetricRow(cut, MotorTemp).Click();

        _fixture.Adapter.Store.RemoveMatchingTopic(NodeTopic);
        _fixture.Adapter.Store.RemoveMatchingTopic(DeviceTopic);
        _topology.Groups.Returns(new Dictionary<string, SpbGroup>(StringComparer.Ordinal));
        _topology.TopologyChanged += Raise.Event<Action>();
        await RefreshOnNextTickAsync(cut);

        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
    }

    [Test]
    public async Task TopologyOnlyChange_RefreshesMetricRowsWithoutATreeVersionChange()
    {
        _node.Metrics = [];
        _node.Devices["dev1"].Metrics = [];
        var cut = RenderExpanded();
        cut.FindAll("[data-metric-name]").Should().BeEmpty();
        var version = _fixture.Adapter.Store.Version;

        _node.Metrics = [Metric("New/Metric", "1")];
        _topology.TopologyChanged += Raise.Event<Action>();
        await RefreshOnNextTickAsync(cut);

        _fixture.Adapter.Store.Version.Should().Be(version);
        cut.FindAll("[data-metric-group='true']").Should().ContainSingle();
        ExpandFirstMetricsGroup(cut);
        FolderRow(cut, "New").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-metric-name='New/Metric']").Should().NotBeEmpty());
    }

    [Test]
    public async Task TopologyChangedBurst_RendersOnceWhenTheCadenceTickRuns()
    {
        _node.Metrics = [];
        var cut = RenderExpanded();
        var version = _fixture.Adapter.Store.Version;
        var rendersBefore = cut.RenderCount;

        _node.Metrics = [Metric("Latest/Value", "9")];
        for (var index = 0; index < 5; index++)
            _topology.TopologyChanged += Raise.Event<Action>();

        cut.RenderCount.Should().BeLessThanOrEqualTo(rendersBefore + 1,
            "a burst queues at most one refresh, not one render per event");

        await RefreshOnNextTickAsync(cut);

        _fixture.Adapter.Store.Version.Should().Be(version);
        ExpandFirstMetricsGroup(cut);
        FolderRow(cut, "Latest").Should().NotBeNull("the next cadence tick serves the whole burst");
    }

    [Test]
    public void TopologyOnlyChange_RefreshesOnTheExistingCadenceTimer()
    {
        _node.Metrics = [];
        _node.Devices["dev1"].Metrics = [];
        var cut = RenderExpanded();
        cut.FindAll("[data-metric-name]").Should().BeEmpty();
        var version = _fixture.Adapter.Store.Version;

        _node.Metrics = [Metric("Timer/Fresh", "5")];
        _topology.TopologyChanged += Raise.Event<Action>();

        cut.WaitForAssertion(
            () => cut.FindAll("[data-metric-group='true']").Should().NotBeEmpty(),
            TimeSpan.FromSeconds(5));
        ExpandFirstMetricsGroup(cut);
        cut.WaitForAssertion(() => cut.FindAll("[data-metric-folder='true']").Should().NotBeEmpty());
        _fixture.Adapter.Store.Version.Should().Be(version);
    }

    [Test]
    public async Task DisposeAsync_UnsubscribesFromTopologyChanges()
    {
        var cut = RenderExpanded();

        await cut.Instance.DisposeAsync();

        _topology.Received(1).TopologyChanged -= Arg.Any<Action>();
    }

    [Test]
    public async Task DisposedBrowser_IgnoresTopologyEventsAndRefreshes()
    {
        var cut = RenderExpanded();
        await cut.Instance.DisposeAsync();
        var renders = cut.RenderCount;

        _topology.TopologyChanged += Raise.Event<Action>();
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.RenderCount.Should().Be(renders);
    }

    [Test]
    public void DesktopMetricRow_WithAChartHandler_AddsTheExactMetric()
    {
        SparkplugChartSelection? captured = null;
        var cut = Render<TopicBrowser>(parameters => parameters
            .Add(component => component.TopologyService, _topology)
            .Add(component => component.OnAddMetricToChart,
                EventCallback.Factory.Create<SparkplugChartSelection>(this, selection => captured = selection)));
        EnsureMudProviders();
        ExpandAll(cut);

        MetricRow(cut, MotorTemp).QuerySelector("button[aria-label='Chart metric Motor/Temp']")!.Click();

        captured.Should().NotBeNull();
        captured!.Topic.Should().Be(SparkplugChartTargets.NodeTopic("plant1", "node1"));
        captured.JsonPath.Should().Be(SparkplugChartTargets.JsonPath(MotorTemp));
        captured.SeriesName.Should().Be(MotorTemp);
    }

    [Test]
    public void PickerMetricRow_WithoutAChartHandler_RendersNoChartAction()
    {
        var cut = RenderExpanded();

        var row = MetricRow(cut, MotorTemp);

        row.QuerySelector(".topic-metric-chart__button").Should().BeNull(
            "the topic picker hosts this browser with no chart handler, so no dead action may render");
    }

    [Test]
    public void ExistingRealTopicSelection_RemainsARealTopic()
    {
        var cut = RenderExpanded();

        cut.FindAll(".topic-tree-row")
            .Single(row => row.GetAttribute("data-topic-path") == NodeTopic)
            .Click();

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.SingleTopic);
        state.Key.Should().Be(NodeTopic);
        state.Metric.Should().BeNull();
    }
}
