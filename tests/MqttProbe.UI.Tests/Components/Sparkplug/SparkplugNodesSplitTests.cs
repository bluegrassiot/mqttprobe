using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesSplitTests : BunitTestContext
{
    [SetUp]
    public void Setup()
    {
        var topology = Substitute.For<ISparkplugTopologyService>();
        topology.Groups.Returns(new Dictionary<string, SpbGroup>());
        Services.AddSingleton(topology);
        Services.AddSingleton(Substitute.For<ISparkplugCommandService>());

        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings());
        Services.AddSparkplugSettings(sparkplugSettings);

        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());
        EnsureMudProviders();
    }

    [Test]
    public void DesktopSplit_RendersTheSharedSplitterWithBothPanes()
    {
        var cut = RenderDesktopNodes();

        var panel = cut.Find(".spb-split-panel");
        panel.ClassList.Should().Contain("app-split-panel");

        var divider = panel.QuerySelector(".divider")!;
        divider.GetAttribute("role").Should().Be("separator");
        divider.GetAttribute("aria-label").Should().Be("Resize panels");
        divider.GetAttribute("aria-orientation").Should().Be("vertical");
        divider.GetAttribute("tabindex").Should().Be("0");

        panel.QuerySelectorAll(":scope > .child-panel").Should().HaveCount(2);
        panel.QuerySelector(".spb-panel-nodes > .spb-split__nodes").Should().NotBeNull();
        panel.QuerySelectorAll(".spb-split__detail").Should().ContainSingle();
        cut.FindAll(".spb-split-wrap").Should().ContainSingle();
    }

    [Test]
    public void DesktopSplit_AppliesBoundedDragInteropToTheNodesWrapper()
    {
        var module = JSInterop.SetupModule("./_content/MqttProbe.UI/js/sparkplug-nodes-split-bounds.js");
        module.SetupVoid("install", _ => true).SetVoidResult();
        module.Setup<int?>("attach", _ => true).SetResult(1);

        var cut = RenderDesktopNodes();

        cut.WaitForAssertion(() => module.VerifyInvoke("attach"));
        module.VerifyInvoke("install", 1);
        module.VerifyInvoke("attach", 1);
    }

    [Test]
    public void StackedLayout_KeepsThePlainSplitWithoutASeparator()
    {
        var cut = Render<SparkplugNodesView>();

        cut.Find(".spb-split").Should().NotBeNull();
        cut.FindAll(".mud-split-panel").Should().BeEmpty();
        cut.FindAll(".divider").Should().BeEmpty();
    }

    private IRenderedComponent<SparkplugNodesView> RenderDesktopNodes()
    {
        var wrapper = Render<CascadingValue<Breakpoint>>(parameters => parameters
            .Add(value => value.Value, Breakpoint.Lg)
            .Add(value => value.IsFixed, true)
            .Add(value => value.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<SparkplugNodesView>(0);
                builder.CloseComponent();
            })));

        return wrapper.FindComponent<SparkplugNodesView>();
    }
}
