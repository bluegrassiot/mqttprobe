using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;
using MudBlazor.Services;

namespace MqttProbe.Tests.Components;

[TestFixture]
public class CertificatePasswordFieldTests : BunitTestContext
{
    [Test]
    public void Renders_PasswordInput()
    {
        var cut = Render<CertificatePasswordField>(
            p => p.Add(x => x.Value, "").Add(x => x.ValueChanged, EventCallback.Factory.Create<string>(this, _ => { })));
        cut.Find("input").GetAttribute("type").Should().Be("password");
    }

    [Test]
    public void ToggleSwitchesVisibility()
    {
        var cut = Render<CertificatePasswordField>(
            p => p.Add(x => x.Value, "secret").Add(x => x.ValueChanged, EventCallback.Factory.Create<string>(this, _ => { })));
        cut.Find("input").GetAttribute("type").Should().Be("password");
        cut.Find("button").Click();
        cut.Find("input").GetAttribute("type").Should().Be("text");
    }
}
