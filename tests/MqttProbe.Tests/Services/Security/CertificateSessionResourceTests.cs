using System.Security.Cryptography.X509Certificates;
using MqttProbe.Services.Security;
using MqttProbe.Tests.Services.Security.TestHelpers;

namespace MqttProbe.Tests.Services.Security;

[TestFixture]
public class CertificateSessionResourceTests
{
    [Test]
    public void Certificate_BeforeSet_ReturnsNull()
    {
        using var resource = new CertificateSessionResource();
        resource.Certificate.Should().BeNull();
    }

    [Test]
    public void Set_SetsCertificate()
    {
        using var resource = new CertificateSessionResource();
        using var cert = TestCertFactory.CreateRsaCert();
        resource.Set(cert);
        resource.Certificate.Should().BeSameAs(cert);
    }

    [Test]
    public void Set_AfterDispose_ThrowsObjectDisposedException()
    {
        var resource = new CertificateSessionResource();
        resource.Dispose();
        using var cert = TestCertFactory.CreateRsaCert();
        var act = () => resource.Set(cert);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void Set_WhenAlreadySet_ThrowsInvalidOperationException()
    {
        using var resource = new CertificateSessionResource();
        using var cert1 = TestCertFactory.CreateRsaCert();
        using var cert2 = TestCertFactory.CreateRsaCert();
        resource.Set(cert1);
        var act = () => resource.Set(cert2);
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Dispose_DisposesHeldCertificate()
    {
        var resource = new CertificateSessionResource();
        var cert = TestCertFactory.CreateRsaCert();
        resource.Set(cert);
        resource.Dispose();
        resource.Certificate.Should().BeNull();
    }

    [Test]
    public void DoubleDispose_IsSafe()
    {
        var resource = new CertificateSessionResource();
        resource.Dispose();
        var act = () => resource.Dispose();
        act.Should().NotThrow();
    }
}
