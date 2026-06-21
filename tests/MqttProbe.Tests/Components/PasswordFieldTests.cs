using MqttProbe.Models.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components;

[TestFixture]
public class PasswordFieldTests : BunitTestContext
{
    [Test]
    public void Renders_WithPasswordInputType_Initially()
    {
        var cut = Render<PasswordField>(p =>
            p.Add(x => x.Connection, new Connection()));

        cut.Find("input[type='password']").Should().NotBeNull();
    }

    [Test]
    public void ToggleVisibility_ChangesInputTypeToText()
    {
        var cut = Render<PasswordField>(p =>
            p.Add(x => x.Connection, new Connection()));

        cut.Find("button").Click();

        cut.Find("input[type='text']").Should().NotBeNull();
    }

    [Test]
    public void ToggleVisibility_Twice_RestoresPasswordType()
    {
        var cut = Render<PasswordField>(p =>
            p.Add(x => x.Connection, new Connection()));

        cut.Find("button").Click();
        cut.Find("button").Click();

        cut.Find("input[type='password']").Should().NotBeNull();
    }

    [Test]
    public void Value_BindsToConnectionPassword()
    {
        var conn = new Connection { Password = "secret123" };
        var cut = Render<PasswordField>(p =>
            p.Add(x => x.Connection, conn));

        // MudTextField renders the bound value as the input's value attribute
        cut.Find("input").GetAttribute("value").Should().Be("secret123");
    }

    [Test]
    public void TypingPassword_UpdatesSuppliedConnection()
    {
        var conn = new Connection();
        var cut = Render<PasswordField>(p =>
            p.Add(x => x.Connection, conn));

        cut.Find("input").Input("newpass");

        conn.Password.Should().Be("newpass");
    }
}
