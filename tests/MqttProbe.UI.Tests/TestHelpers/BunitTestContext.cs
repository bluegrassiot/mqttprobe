using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Services.Platform;
using MudBlazor;
using MudBlazor.Services;
using BunitContext = Bunit.BunitContext;

namespace MqttProbe.UI.Tests.TestHelpers;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public abstract class BunitTestContext : BunitContext
{
    protected BunitAuthorizationContext AuthorizationContext { get; }

    protected BunitTestContext()
    {
        Services.AddMudServices();
        Services.AddLogging();
        Services.AddSingleton(Substitute.For<IClipboardService>());
        Services.AddScoped<MqttProbe.UI.Components.Browser.ITopicSelectionStore>(provider =>
            provider.GetService<MqttProbe.Core.Services.Mqtt.IMessageStoreManager>() is { } manager
                ? new MqttProbe.UI.Components.Browser.MessageStoreSelectionAdapter(manager)
                : MqttProbe.UI.Tests.Components.Browser.TopicTreeSelectionTestAdapter.Create());
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Auth support (user is unauthenticated by default)
        AuthorizationContext = this.AddAuthorization();
    }

    protected void EnsureMudProviders() => Render<MudPopoverProvider>();
}
