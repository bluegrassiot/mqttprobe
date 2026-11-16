using MqttProbe.Web;

namespace MqttProbe.UI.Tests;

[TestFixture]
public sealed class NullReferenceCaptureTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        NullReferenceCapture.ResetForTests();
        _root = Path.Combine(Path.GetTempPath(), "opencode", "nre-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        NullReferenceCapture.ResetForTests();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Test]
    public void DisabledGateDoesNotInstallAHandlerOrCreateAFile()
    {
        var installed = NullReferenceCapture.TryInstall(null, _root, out var error);

        Assert.That(installed, Is.False);
        Assert.That(error, Is.EqualTo("disabled"));
        Assert.That(NullReferenceCapture.IsEnabledForTests, Is.False);
        Assert.That(Directory.EnumerateFiles(_root), Is.Empty);
    }

    [Test]
    public void RejectsRelativeOutsideAndEscapingPaths()
    {
        var relative = NullReferenceCapture.TryInstall("nre.log", _root, out _);
        var outside = NullReferenceCapture.TryInstall(Path.Combine(Path.GetTempPath(), "outside-nre.log"), _root, out _);
        var escaping = NullReferenceCapture.TryInstall(Path.Combine(_root, "..", "escape.log"), _root, out _);
        var directory = Directory.CreateDirectory(Path.Combine(_root, "dir")).FullName;
        var directoryPath = NullReferenceCapture.TryInstall(directory, _root, out _);

        Assert.That(new[] { relative, outside, escaping, directoryPath }, Is.All.False);
        Assert.That(NullReferenceCapture.IsEnabledForTests, Is.False);
    }

    [Test]
    public void CapturesTypeAndStackWithoutTheMessageWithinCaps()
    {
        var path = Path.Combine(_root, "nre.log");
        Assert.That(NullReferenceCapture.TryInstall(path, _root, out _), Is.True);

        for (var index = 0; index < 100; index++)
            NullReferenceCapture.CaptureForTests(new NullReferenceException("SECRET_MESSAGE"));

        var text = File.ReadAllText(path);
        Assert.Multiple(() =>
        {
            Assert.That(NullReferenceCapture.RecordCountForTests, Is.LessThanOrEqualTo(NullReferenceCapture.MaxRecords));
            Assert.That(NullReferenceCapture.ByteCountForTests, Is.LessThanOrEqualTo(NullReferenceCapture.MaxBytes));
            Assert.That(new FileInfo(path).Length, Is.EqualTo(NullReferenceCapture.ByteCountForTests));
            Assert.That(text, Does.Contain("Type: System.NullReferenceException"));
            Assert.That(text, Does.Contain("Stack:"));
            Assert.That(text, Does.Not.Contain("SECRET_MESSAGE"));
        });
    }

    [Test]
    public void ReentrantCaptureIsIgnored()
    {
        var path = Path.Combine(_root, "nre.log");
        Assert.That(NullReferenceCapture.TryInstall(path, _root, out _), Is.True);

        NullReferenceCapture.CaptureForTests(
            new NullReferenceException("outer"),
            () => NullReferenceCapture.CaptureForTests(new NullReferenceException("inner")));

        var text = File.ReadAllText(path);
        Assert.Multiple(() =>
        {
            Assert.That(NullReferenceCapture.RecordCountForTests, Is.EqualTo(1));
            Assert.That(text, Does.Not.Contain("inner"));
        });
    }

    [Test]
    public void SecondInstallIsRejected()
    {
        Assert.That(NullReferenceCapture.TryInstall(Path.Combine(_root, "first.log"), _root, out _), Is.True);
        var second = NullReferenceCapture.TryInstall(Path.Combine(_root, "second.log"), _root, out var error);

        Assert.That(second, Is.False);
        Assert.That(error, Is.EqualTo("already installed"));
        Assert.That(File.Exists(Path.Combine(_root, "second.log")), Is.False);
    }
}
