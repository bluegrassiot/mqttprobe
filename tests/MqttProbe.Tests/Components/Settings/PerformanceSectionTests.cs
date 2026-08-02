using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Settings;
using MqttProbe.Models.Configuration;
using MqttProbe.Services.Configuration;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Settings;

[TestFixture]
public class PerformanceSectionTests : BunitTestContext
{
    private ISettingsStore _mockStore = null!;

    [SetUp]
    public void Setup()
    {
        _mockStore = Substitute.For<ISettingsStore>();
        _mockStore.Config.Returns(new AppConfiguration
        {
            Performance = new PerformanceSettings
            {
                MaxStoredMessages = 10_000,
                MaxMessagesPerSecond = 50_000
            }
        });
        Services.AddSingleton(_mockStore);
        EnsureMudProviders();
    }

    [Test]
    public async Task MaxStoredMessages_Change_UsesNewSetter()
    {
        _mockStore.SetMaxStoredMessagesAsync(Arg.Any<int>()).Returns(Task.CompletedTask);
        var cut = Render<PerformanceSection>();

        var field = cut.FindComponents<MudNumericField<int>>()
            .First(f => f.Instance.Label == "Max stored messages");
        field.Find("input").Change("5000");

        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockStore.Received(1).SetMaxStoredMessagesAsync(5000);
    }

    [Test]
    public async Task MaxMessagesPerSecond_Change_UsesNewSetter()
    {
        _mockStore.SetMaxMessagesPerSecondAsync(Arg.Any<int>()).Returns(Task.CompletedTask);
        var cut = Render<PerformanceSection>();

        var field = cut.FindComponents<MudNumericField<int>>()
            .First(f => f.Instance.Label == "Max messages per second");
        field.Find("input").Change("2000");

        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockStore.Received(1).SetMaxMessagesPerSecondAsync(2000);
    }

    [Test]
    public void MaxDisplayedMessages_Field_Renders()
    {
        var cut = Render<PerformanceSection>();

        cut.FindComponents<MudNumericField<int>>()
            .Should().Contain(f => f.Instance.Label == "Max displayed messages");
    }

    [Test]
    public async Task MaxDisplayedMessages_Change_UsesNewSetter()
    {
        _mockStore.SetMaxDisplayMessagesAsync(Arg.Any<int>()).Returns(Task.CompletedTask);
        var cut = Render<PerformanceSection>();

        var field = cut.FindComponents<MudNumericField<int>>()
            .First(f => f.Instance.Label == "Max displayed messages");
        field.Find("input").Change("300");

        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockStore.Received(1).SetMaxDisplayMessagesAsync(300);
    }

    [Test]
    public async Task MaxTopicNodes_Change_UsesNewSetter()
    {
        _mockStore.SetMaxTopicNodesAsync(Arg.Any<int>()).Returns(Task.CompletedTask);
        var cut = Render<PerformanceSection>();

        var field = cut.FindComponents<MudNumericField<int>>()
            .First(f => f.Instance.Label == "Max topic nodes");
        field.Find("input").Change("4000");

        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockStore.Received(1).SetMaxTopicNodesAsync(4000);
    }
}
