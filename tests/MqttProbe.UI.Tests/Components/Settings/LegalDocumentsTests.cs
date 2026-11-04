using MqttProbe.UI.Components.Settings;

namespace MqttProbe.UI.Tests.Components.Settings;

[TestFixture]
public class LegalDocumentsTests
{
    [Test]
    public void Ui_Assembly_Embeds_Both_Legal_Documents_Under_The_Agreed_Names()
    {
        var names = typeof(AboutSection).Assembly.GetManifestResourceNames();

        names.Should().Contain(LegalDocuments.LicenseResourceName,
            "MqttProbe.UI.csproj must embed LICENSE with LogicalName MqttProbe.UI.Legal.LICENSE");
        names.Should().Contain(LegalDocuments.NoticesResourceName,
            "MqttProbe.UI.csproj must embed NOTICES with LogicalName MqttProbe.UI.Legal.NOTICES");
    }

    [Test]
    public void License_Reads_The_Apache_Licence_Text()
    {
        var text = LegalDocuments.ReadLicense();

        text.Should().Contain("Apache License");
        text.Should().Contain("Version 2.0");
    }

    [Test]
    public void Notices_Reads_The_Third_Party_Notices_Text()
    {
        var text = LegalDocuments.ReadNotices();

        text.Should().Contain("third-party components");
        text.Should().Contain("EPL-2.0");
    }
}
