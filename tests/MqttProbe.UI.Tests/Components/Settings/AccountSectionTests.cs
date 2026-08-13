using Bunit;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Components.Settings;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Settings;

[TestFixture]
public class AccountSectionTests : BunitTestContext
{
    [SetUp]
    public void Setup()
    {
        AuthorizationContext.SetAuthorized("admin").SetRoles(AppRoles.Admin);
        EnsureMudProviders();
    }

    [Test]
    public void ChangePassword_ButtonHasHref()
    {
        var cut = Render<AccountSection>();

        var link = cut.FindAll("a, button")
            .First(b => b.TextContent.Contains("Change password"));
        link.GetAttribute("href").Should().Be("/change-password");
    }

    [Test]
    public void ChangePassword_Hidden_WhenNotAuthorized()
    {
        AuthorizationContext.SetNotAuthorized();

        var cut = Render<AccountSection>();

        cut.FindAll("a, button")
            .Should().NotContain(b => b.TextContent.Contains("Change password"));
    }
}
