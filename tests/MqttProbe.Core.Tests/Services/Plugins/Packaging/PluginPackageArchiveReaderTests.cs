using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins.Packaging;
using MqttProbe.Core.Services.Plugins.Protobuf;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Tests.Services.Plugins.Packaging;

[TestFixture]
public sealed class PluginPackageArchiveReaderTests
{
    private string _stagingRoot = string.Empty;
    private string _archivePath = string.Empty;
    private string _extractPath = string.Empty;

    private static string TestOutputDir => NUnit.Framework.TestContext.CurrentContext.TestDirectory;

    private static string FixtureAssemblyPath =>
        Path.Combine(Path.GetFullPath(Path.Combine(TestOutputDir, "PluginFixtures")), "MqttProbe.PluginLoader.Fixtures.dll");

    private static void RequireFixtureAssembly()
    {
        if (!File.Exists(FixtureAssemblyPath))
        {
            Assert.Ignore("MqttProbe.PluginLoader.Fixtures.dll not found; build the solution before running these tests.");
        }
    }

    [SetUp]
    public void SetUp()
    {
        _stagingRoot = Path.Combine(Path.GetTempPath(), "mqttprobe-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stagingRoot);
        _archivePath = Path.Combine(_stagingRoot, "package.zip");
        _extractPath = Path.Combine(_stagingRoot, "package");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_stagingRoot))
        {
            Directory.Delete(_stagingRoot, recursive: true);
        }
    }

    private PluginPackageArchiveReader CreateReader(
        bool allowBinary = false,
        PluginArchiveLimits? limits = null)
    {
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");

        return new PluginPackageArchiveReader(
            new PluginConfig { AllowBinaryPackages = allowBinary },
            appInfo,
            limits ?? new PluginArchiveLimits(),
            NullLoggerFactory.Instance);
    }

    private Task<PluginPackageArchiveReadResult> ReadAsync(
        Stream package,
        PluginPackageArchiveReader? reader = null,
        CancellationToken ct = default) =>
        (reader ?? CreateReader()).ReadAsync(package, _archivePath, _extractPath, ct);

    private static MemoryStream ArchiveWith(params (string Name, string Content)[] entries)
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    private static string PluginManifestJson(
        string id,
        string version,
        string kind = "protobuf-schemas",
        string name = "Demo Schemas") =>
        $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "version": "{{version}}",
          "kind": "{{kind}}"
        }
        """;

    private static string SchemaManifestJson(string messageType) =>
        $$"""
        {
          "schemas": [
            {
              "files": [ "demo.proto" ],
              "topicPattern": "demo/+/reading",
              "messageType": "{{messageType}}"
            }
          ]
        }
        """;

    private static MemoryStream SchemaPackageWith(params (string Name, string Content)[] entries)
    {
        var entriesWithManifest = new (string Name, string Content)[entries.Length + 1];
        entriesWithManifest[0] =
            (PluginManifestValidator.FileName, PluginManifestJson("demo", "1.0.0"));
        entries.CopyTo(entriesWithManifest, 1);

        return ArchiveWith(entriesWithManifest);
    }

    private static MemoryStream AssemblyPackageWithPrimaryDll(
        string id,
        string dllSourcePath,
        string primaryDllName,
        string version = "1.0.0")
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(
                zip.CreateEntry(PluginManifestValidator.FileName).Open(), Encoding.UTF8))
            {
                writer.Write(PluginManifestJson(id, version, PluginPackageKinds.Assembly, "Demo Binary"));
            }

            zip.CreateEntryFromFile(dllSourcePath, primaryDllName);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static void BuildAssemblyWithUnresolvableDependency(string outputPath)
    {
        var metadata = new MetadataBuilder();

        metadata.AddModule(
            generation: 0,
            moduleName: metadata.GetOrAddString(Path.GetFileName(outputPath)),
            mvid: metadata.GetOrAddGuid(Guid.NewGuid()),
            encId: default,
            encBaseId: default);

        metadata.AddAssembly(
            name: metadata.GetOrAddString(Path.GetFileNameWithoutExtension(outputPath)),
            version: new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: 0,
            hashAlgorithm: AssemblyHashAlgorithm.None);

        var fakeAssemblyRef = metadata.AddAssemblyReference(
            name: metadata.GetOrAddString("Nonexistent.Fake.Dependency"),
            version: new Version(1, 0, 0, 0),
            culture: default,
            publicKeyOrToken: default,
            flags: 0,
            hashValue: default);

        var fakeBaseTypeRef = metadata.AddTypeReference(
            resolutionScope: fakeAssemblyRef,
            @namespace: metadata.GetOrAddString("FakeNamespace"),
            name: metadata.GetOrAddString("FakeBase"));

        metadata.AddTypeDefinition(
            attributes: default,
            @namespace: default,
            name: metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));

        metadata.AddTypeDefinition(
            attributes: TypeAttributes.Public | TypeAttributes.Class,
            @namespace: metadata.GetOrAddString(string.Empty),
            name: metadata.GetOrAddString("DemoPlugin"),
            baseType: fakeBaseTypeRef,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));

        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
            new MetadataRootBuilder(metadata),
            new BlobBuilder());

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);

        using (var file = new FileStream(outputPath, FileMode.Create))
        {
            peBlob.WriteContentTo(file);
        }
    }

    private static PluginInstallOutcome Failure(PluginPackageArchiveReadResult result)
    {
        result.Failure.Should().NotBeNull();
        var failure = result.Failure!;
        failure.Succeeded.Should().BeFalse();
        failure.Manifest.Should().BeNull();
        failure.InstallPath.Should().BeNull();
        failure.RequiresRestart.Should().BeFalse();
        return failure;
    }

    [Test]
    public async Task Copies_A_Compressed_Package_Within_The_Configured_Bound()
    {
        using var package = PluginPackageTestData.SchemaPackage();
        package.Length.Should().BeLessThan(4_096);

        var result = await ReadAsync(package, CreateReader(limits: new PluginArchiveLimits
        {
            MaxCompressedBytes = 4_096
        }));

        result.Failure.Should().BeNull();
        File.Exists(_archivePath).Should().BeTrue();
    }

    [Test]
    public async Task Returns_The_Exact_Compressed_Size_Failure_Message()
    {
        using var package = PluginPackageTestData.SchemaPackage(paddingBytes: 4_096);
        package.Length.Should().BeGreaterThan(512);

        var result = await ReadAsync(package, CreateReader(limits: new PluginArchiveLimits
        {
            MaxCompressedBytes = 512
        }));

        Failure(result).Error.Should().Be("Package exceeds the maximum upload size of 512 bytes.");
    }

    [Test]
    public async Task Throws_For_Invalid_Zip_Input()
    {
        using var package = new MemoryStream("not a zip"u8.ToArray());

        var act = async () => await ReadAsync(package);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task Returns_A_Failure_When_The_Root_Manifest_Is_Missing()
    {
        using var package = ArchiveWith(("demo.proto", PluginPackageTestData.DemoProto));

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Be("Package does not contain mqttprobe-plugin.json at its root.");
    }

    [Test]
    public async Task Returns_A_Failure_When_The_Manifest_Is_Misplaced()
    {
        using var package = ArchiveWith(
            ("nested/mqttprobe-plugin.json", PluginManifestJson("demo", "1.0.0")),
            ("demo.proto", PluginPackageTestData.DemoProto));

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Contain("at its root");
    }

    [Test]
    public async Task Returns_Manifest_Validation_Failures_Through_Failure()
    {
        using var package = ArchiveWith(
            (PluginManifestValidator.FileName, PluginManifestJson("Bad Id", "1.0.0")));

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Contain("Package id");
    }

    [Test]
    public async Task Rejects_Binary_Packages_When_They_Are_Disabled()
    {
        using var package = PluginPackageTestData.AssemblyPackage(
            "demoplugin", typeof(IMqttProbePlugin).Assembly.Location);

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Contain("AllowBinaryPackages");
    }

    [Test]
    public async Task Rejects_Binary_Packages_When_Assembly_Inspection_Is_Unsupported()
    {
        if (PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are supported on this platform.");
        }

        using var package = PluginPackageTestData.AssemblyPackage(
            "demoplugin", typeof(IMqttProbePlugin).Assembly.Location);

        var result = await ReadAsync(package, CreateReader(allowBinary: true));

        Failure(result).Error.Should().Contain("not supported on this platform");
    }

    [Test]
    public async Task Rejects_Unsafe_Archive_Paths()
    {
        using var package = SchemaPackageWith(("../escape.proto", PluginPackageTestData.DemoProto));

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Contain("unsafe entry path");
    }

    [Test]
    public async Task Rejects_A_File_Extension_Not_Allowed_For_The_Package_Kind()
    {
        using var package = SchemaPackageWith(("evil.dll", "MZ"));

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Contain(".dll");
    }

    [Test]
    public async Task Rejects_Archives_That_Exceed_The_Entry_Count_Limit()
    {
        using var package = PluginPackageTestData.SchemaPackage();
        var result = await ReadAsync(package, CreateReader(limits: new PluginArchiveLimits { MaxEntries = 2 }));

        Failure(result).Error.Should().Contain("entries");
    }

    [Test]
    public async Task Rejects_Archives_That_Exceed_Declared_Uncompressed_Limits()
    {
        using var package = SchemaPackageWith(("demo.proto", new string('a', 5_000)));
        var result = await ReadAsync(package, CreateReader(limits: new PluginArchiveLimits
        {
            MaxUncompressedBytes = 1_000
        }));

        Failure(result).Error.Should().Contain("uncompressed");
    }

    [Test]
    public async Task Rejects_Archives_That_Exceed_Declared_Compression_Ratio_Limits()
    {
        using var package = SchemaPackageWith(("demo.proto", new string('a', 2_000_000)));
        var result = await ReadAsync(package, CreateReader(limits: new PluginArchiveLimits
        {
            MaxCompressionRatio = 10
        }));

        Failure(result).Error.Should().Contain("compression ratio");
    }

    [Test]
    public async Task Throws_The_Specific_Exception_When_Actual_Expansion_Exceeds_The_Limit()
    {
        using var package = PluginPackageTestData.SchemaPackageWithFalsifiedPaddingLength(realPaddingBytes: 200_000);
        var reader = CreateReader(limits: new PluginArchiveLimits
        {
            MaxUncompressedBytes = 1_000,
            MaxCompressedBytes = 33_554_432
        });

        var act = async () => await ReadAsync(package, reader);

        await act.Should().ThrowAsync<PluginPackageExpansionLimitExceededException>()
            .WithMessage("Package expands to more than the permitted 1000 uncompressed bytes.");
    }

    [Test]
    public async Task Returns_A_Failure_When_The_Protobuf_Schema_Manifest_Is_Absent()
    {
        using var package = ArchiveWith(
            (PluginManifestValidator.FileName, PluginManifestJson("demo", "1.0.0")),
            ("demo.proto", PluginPackageTestData.DemoProto));

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Contain(ProtobufSchemaFolderLoader.ManifestFileName);
    }

    [Test]
    public async Task Returns_A_Failure_When_The_Protobuf_Schema_Manifest_Is_Malformed()
    {
        using var package = SchemaPackageWith(
            (ProtobufSchemaFolderLoader.ManifestFileName, "{"),
            ("demo.proto", PluginPackageTestData.DemoProto));

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Contain("not valid JSON");
    }

    [Test]
    public async Task Returns_A_Failure_When_The_Protobuf_Schema_Manifest_Is_Empty()
    {
        using var package = SchemaPackageWith(
            (ProtobufSchemaFolderLoader.ManifestFileName, "{\"schemas\": []}"),
            ("demo.proto", PluginPackageTestData.DemoProto));

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Contain("declares no schemas");
    }

    [Test]
    public async Task Returns_A_Failure_When_No_Compiled_Schemas_Are_Available()
    {
        using var package = SchemaPackageWith(
            (ProtobufSchemaFolderLoader.ManifestFileName, SchemaManifestJson("demo.Reading")));

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Contain("No schemas compiled");
    }

    [Test]
    public async Task Returns_A_Failure_When_A_Declared_Message_Type_Cannot_Be_Resolved()
    {
        using var package = PluginPackageTestData.SchemaPackage(messageType: "demo.NotThere");

        var result = await ReadAsync(package);

        Failure(result).Error.Should().Contain("demo.NotThere");
    }

    [Test]
    public async Task Returns_Success_With_The_Manifest_And_Exact_Archive_And_Extraction_Paths()
    {
        using var package = PluginPackageTestData.SchemaPackage();

        var result = await ReadAsync(package);

        result.Failure.Should().BeNull();
        result.Manifest.Should().Be(new PluginPackageManifest
        {
            Id = "demo",
            Name = "Demo Schemas",
            Version = "1.0.0",
            Kind = PluginPackageKinds.ProtobufSchemas
        });
        result.ArchivePath.Should().Be(_archivePath);
        result.ExtractPath.Should().Be(_extractPath);
        File.Exists(Path.Combine(_extractPath, "demo.proto")).Should().BeTrue();
    }

    [Test]
    public async Task Returns_Success_For_Valid_Assembly_Content()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        RequireFixtureAssembly();

        using var package = PluginPackageTestData.AssemblyPackage("demoplugin", FixtureAssemblyPath);

        var result = await ReadAsync(package, CreateReader(allowBinary: true));

        result.Failure.Should().BeNull();
        result.Manifest.Should().Be(new PluginPackageManifest
        {
            Id = "demoplugin",
            Name = "Demo Binary",
            Version = "1.0.0",
            Kind = PluginPackageKinds.Assembly
        });
    }

    [Test]
    public async Task Returns_A_Failure_For_A_Misnamed_Primary_Assembly()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        using var package = AssemblyPackageWithPrimaryDll(
            "demoplugin", typeof(IMqttProbePlugin).Assembly.Location, "something-else.dll");

        var result = await ReadAsync(package, CreateReader(allowBinary: true));

        Failure(result).Error.Should().Contain("demoplugin.dll");
    }

    [Test]
    public async Task Returns_A_Failure_When_An_Assembly_Has_No_Plugin_Implementation()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        using var package = PluginPackageTestData.AssemblyPackage(
            "demoplugin", typeof(IMqttProbePlugin).Assembly.Location);

        var result = await ReadAsync(package, CreateReader(allowBinary: true));

        Failure(result).Error.Should().Contain("IMqttProbePlugin");
    }

    [Test]
    public async Task Returns_A_Failure_For_An_Unresolved_Assembly_Dependency()
    {
        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            Assert.Ignore("Assembly plugins are not supported on this platform.");
        }

        var sourcePath = Path.Combine(_stagingRoot, "demoplugin-source.dll");
        BuildAssemblyWithUnresolvableDependency(sourcePath);
        using var package = PluginPackageTestData.AssemblyPackage("demoplugin", sourcePath);

        var result = await ReadAsync(package, CreateReader(allowBinary: true));

        Failure(result).Error.Should().Contain("Nonexistent.Fake.Dependency");
    }

    [Test]
    public async Task Propagates_Cancellation_Instead_Of_Returning_A_Failure()
    {
        using var package = PluginPackageTestData.SchemaPackage();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await ReadAsync(package, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
