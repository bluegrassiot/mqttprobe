using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class KeyboardInteropLifecycleTests
{
    private static ElementReference Root => new("root");

    private static IEnumerable<TestCaseData> Subjects()
    {
        foreach (var subject in KeyboardInteropSubjects.All())
            yield return new TestCaseData(subject);
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Attach_ImportsTheModuleAndPassesTheRoot(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = subject.Create(runtime);

        await subject.AttachAsync(interop, Root);

        runtime.ImportedPaths.Should().Equal(subject.ModuleUrl);
        runtime.Module.Identifiers.Should().Equal("attach");
        runtime.Module.Arguments[0].Should().Equal(Root);
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Attach_Twice_AttachesOnce(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = subject.Create(runtime);

        await subject.AttachAsync(interop, Root);
        await subject.AttachAsync(interop, Root);

        runtime.ImportedPaths.Should().Equal(subject.ModuleUrl);
        runtime.Module.Identifiers.Should().Equal("attach");
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Detach_WithoutAnAttachment_DoesNothing(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = subject.Create(runtime);

        await subject.DetachAsync(interop);

        runtime.ImportedPaths.Should().BeEmpty();
        runtime.Module.Identifiers.Should().BeEmpty();
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Detach_Twice_ReleasesTheAttachmentOnce(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = subject.Create(runtime);
        await subject.AttachAsync(interop, Root);

        await subject.DetachAsync(interop);
        await subject.DetachAsync(interop);

        runtime.Module.Identifiers.Should().Equal("attach", "detach");
        runtime.Module.Arguments[1].Should().Equal(1);
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Reattach_AfterDetach_TakesAFreshToken(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = subject.Create(runtime);
        await subject.AttachAsync(interop, Root);
        await subject.DetachAsync(interop);

        await subject.AttachAsync(interop, Root);
        await subject.DetachAsync(interop);

        runtime.Module.Arguments[1].Should().Equal(1);
        runtime.Module.Arguments[3].Should().Equal(2);
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Dispose_DetachesThenDisposesTheModuleOnce(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = subject.Create(runtime);
        await subject.AttachAsync(interop, Root);

        await interop.DisposeAsync();
        await interop.DisposeAsync();
        await subject.AttachAsync(interop, Root);
        await subject.DetachAsync(interop);

        runtime.Module.Identifiers.Should().Equal("attach", "detach");
        runtime.Module.DisposeCount.Should().Be(1);
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Dispose_BeforeImport_DoesNothing(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = subject.Create(runtime);

        await interop.DisposeAsync();

        runtime.ImportedPaths.Should().BeEmpty();
        runtime.Module.Identifiers.Should().BeEmpty();
        runtime.Module.DisposeCount.Should().Be(0);
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Dispose_AfterExplicitDetach_DisposesTheModuleWithoutDetachingAgain(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = subject.Create(runtime);
        await subject.AttachAsync(interop, Root);
        await subject.DetachAsync(interop);

        await interop.DisposeAsync();

        runtime.Module.Identifiers.Should().Equal("attach", "detach");
        runtime.Module.DisposeCount.Should().Be(1);
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Attach_WhenImportIsUnavailable_DoesNothing(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime { ModuleUnavailable = true };
        var interop = subject.Create(runtime);

        await subject.AttachAsync(interop, Root);
        await subject.DetachAsync(interop);

        runtime.ImportedPaths.Should().Equal(subject.ModuleUrl);
        runtime.Module.Identifiers.Should().BeEmpty();
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Attach_WhenImportDisconnects_Propagates(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime { DisconnectedOnImport = true };
        var interop = subject.Create(runtime);

        Func<Task> attach = () => subject.AttachAsync(interop, Root);

        await attach.Should().ThrowAsync<JSDisconnectedException>();
        runtime.ImportedPaths.Should().Equal(subject.ModuleUrl);
        runtime.Module.Identifiers.Should().BeEmpty();
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Detach_WhenDisconnected_ClearsTheAttachmentAndPropagates(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        runtime.Module.DisconnectedOn.Add("detach");
        var interop = subject.Create(runtime);
        await subject.AttachAsync(interop, Root);

        Func<Task> detach = () => subject.DetachAsync(interop);

        await detach.Should().ThrowAsync<JSDisconnectedException>();
        await interop.DisposeAsync();
        runtime.Module.Identifiers.Should().Equal("attach", "detach");
        runtime.Module.DisposeCount.Should().Be(1);
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Dispose_WhenDetachDisconnects_SwallowsItAndSkipsModuleDisposal(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        runtime.Module.DisconnectedOn.Add("detach");
        var interop = subject.Create(runtime);
        await subject.AttachAsync(interop, Root);

        await interop.DisposeAsync();

        runtime.Module.Identifiers.Should().Equal("attach", "detach");
        runtime.Module.DisposeCount.Should().Be(0);
    }

    [TestCaseSource(nameof(Subjects))]
    public async Task Dispose_WhenModuleDisconnects_SwallowsIt(KeyboardInteropSubject subject)
    {
        var runtime = new FakeKeyboardJSRuntime();
        runtime.Module.DisconnectedOnDispose = true;
        var interop = subject.Create(runtime);
        await subject.AttachAsync(interop, Root);

        await interop.DisposeAsync();

        runtime.Module.Identifiers.Should().Equal("attach", "detach");
        runtime.Module.DisposeCount.Should().Be(1);
    }
}
