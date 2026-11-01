using System.IO.Compression;
using MQTTnet;

namespace MqttProbe.Core.Tests.Services.Plugins.Protobuf;

internal static class ChirpStackSampleFixture
{
    public static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "samples", "protobuf")))
            root = root.Parent;
        root.Should().NotBeNull("repo root with samples/protobuf must be found");

        return root.FullName;
    }

    public static string SamplesProtobufDir() => Path.Combine(RepoRoot(), "samples", "protobuf");

    public static string ChirpStackSampleDir() => Path.Combine(SamplesProtobufDir(), "chirpstack");

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(source))
            CopyDirectory(sub, Path.Combine(destination, Path.GetFileName(sub)));
    }

    public static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    public static MemoryStream PackSampleAsPackage()
    {
        var buffer = new MemoryStream();
        var sourceDir = ChirpStackSampleDir();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                if (relative.EndsWith(".b64", StringComparison.OrdinalIgnoreCase))
                    continue;

                zip.CreateEntryFromFile(file, relative);
            }
        }

        buffer.Position = 0;
        return buffer;
    }
}
