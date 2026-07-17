using Microsoft.Extensions.Logging;
using MqttProbe.Services.Security;

namespace MqttProbe.Tests.Services.Security;

[TestFixture]
public class CertificateSessionQuarantineTests
{
    [Test]
    public void Quarantine_LogsWarning()
    {
        var logger = Substitute.For<ILogger<CertificateSessionQuarantine>>();
        var quarantine = new CertificateSessionQuarantine(logger);
        var resource = new CertificateSessionResource();

        quarantine.Quarantine(resource, "test reason");

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => (Convert.ToString(o) ?? "").Contains("test reason")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public void Quarantine_AcceptsMultipleResources()
    {
        var logger = Substitute.For<ILogger<CertificateSessionQuarantine>>();
        var quarantine = new CertificateSessionQuarantine(logger);

        quarantine.Quarantine(new CertificateSessionResource(), "first");
        quarantine.Quarantine(new CertificateSessionResource(), "second");
    }

    [Test]
    public void Quarantine_NullResource_ThrowsArgumentNullException()
    {
        var logger = Substitute.For<ILogger<CertificateSessionQuarantine>>();
        var quarantine = new CertificateSessionQuarantine(logger);

        var act = () => quarantine.Quarantine(null!, "reason");
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Quarantine_EmptyReason_ThrowsArgumentException()
    {
        var logger = Substitute.For<ILogger<CertificateSessionQuarantine>>();
        var quarantine = new CertificateSessionQuarantine(logger);

        var act = () => quarantine.Quarantine(new CertificateSessionResource(), "");
        act.Should().Throw<ArgumentException>();
    }
}
