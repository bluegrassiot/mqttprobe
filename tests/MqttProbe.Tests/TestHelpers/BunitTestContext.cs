using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Services.Platform;
using MudBlazor;
using MudBlazor.Services;
using BunitContext = Bunit.BunitContext;

namespace MqttProbe.Shared.Tests.TestHelpers;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public abstract class BunitTestContext : BunitContext
{
    protected BunitAuthorizationContext AuthorizationContext { get; }

    protected BunitTestContext()
    {
        Services.AddMudServices();
        Services.AddLogging();
        Services.AddSingleton(Substitute.For<IClipboardService>());
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Auth support (user is unauthenticated by default)
        AuthorizationContext = this.AddAuthorization();
    }

    protected void EnsureMudProviders() => Render<MudPopoverProvider>();
}
