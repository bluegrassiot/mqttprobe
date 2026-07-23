extern alias ProtoReflection;

using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;
using DescriptorProto = ProtoReflection::Google.Protobuf.Reflection.DescriptorProto;
using EnumDescriptorProto = ProtoReflection::Google.Protobuf.Reflection.EnumDescriptorProto;
using FileDescriptorSet = ProtoReflection::Google.Protobuf.Reflection.FileDescriptorSet;

namespace MqttProbe.Services.Plugins.Protobuf;

public sealed class ProtobufSchemaRegistry
{
    private readonly List<(string Filter, DescriptorProto Message)> _routes = [];
    private readonly Dictionary<string, DescriptorProto> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDescriptorProto> _enums = new(StringComparer.Ordinal);
    private readonly List<string> _diagnostics = [];

    public ProtobufSchemaRegistry(ProtobufSchemaManifest manifest, string schemaRoot, ILogger? logger = null)
        : this([new ProtobufSchemaSource(manifest, schemaRoot)], logger)
    {
    }

    public ProtobufSchemaRegistry(IReadOnlyList<ProtobufSchemaSource> sources, ILogger? logger = null)
    {
        // Each source is parsed against its own import root. Sharing one FileDescriptorSet
        // would let two unrelated schema sets resolve each other's imports — so a vendor
        // shipping its own common/common.proto could silently shadow another's.
        foreach (var source in sources)
        {
            if (source.Manifest.Schemas.Count == 0)
                continue;

            LoadSource(source, logger);
        }
    }

    private void LoadSource(ProtobufSchemaSource source, ILogger? logger)
    {
        var set = new FileDescriptorSet();
        set.AddImportPath(source.SchemaRoot);

        foreach (var file in source.Manifest.Schemas.SelectMany(m => m.Files))
        {
            if (Path.IsPathRooted(file))
            {
                var rootedPathError = $"ERROR: schema file '{file}' is a rooted (absolute) path; " +
                                       "'files' entries must be relative to the manifest's own directory.";
                _diagnostics.Add(rootedPathError);
                logger?.LogError("protobuf schema: {Detail}", rootedPathError);
                continue;
            }
            set.Add(file, includeInOutput: true);
        }

        set.Process();

        foreach (var error in set.GetErrors())
        {
            var line = $"{(error.IsWarning ? "WARN" : "ERROR")} {error.ErrorNumber}: {error.File}#{error.LineNumber}: {error.Message}";
            _diagnostics.Add(line);
            if (error.IsWarning)
                logger?.LogWarning("protobuf schema: {Detail}", line);
            else
                logger?.LogError("protobuf schema: {Detail}", line);
        }

        foreach (var file in set.Files)
        {
            var package = string.IsNullOrEmpty(file.Package) ? "" : "." + file.Package;
            foreach (var message in file.MessageTypes)
                IndexMessage(package, message, source.SchemaRoot, logger);
            foreach (var @enum in file.EnumTypes)
                IndexEnum($"{package}.{@enum.Name}", @enum, source.SchemaRoot, logger);
        }

        BuildRoutingTable(source.Manifest.Schemas, logger);
    }

    public bool HasAnySchemas => _routes.Count > 0;

    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public bool TryResolveByTopic(string topic, out DescriptorProto messageType)
    {
        foreach (var (filter, message) in _routes)
        {
            if (MqttTopicMatcher.Matches(topic, filter))
            {
                messageType = message;
                return true;
            }
        }
        messageType = null!;
        return false;
    }

    public bool TryResolveMessage(string fullyQualifiedName, out DescriptorProto messageType) =>
        _messages.TryGetValue(Normalize(fullyQualifiedName), out messageType!);

    public bool TryResolveEnum(string fullyQualifiedName, out EnumDescriptorProto enumType) =>
        _enums.TryGetValue(Normalize(fullyQualifiedName), out enumType!);

    private void BuildRoutingTable(IReadOnlyList<ProtobufSchemaMapping> mappings, ILogger? logger)
    {
        foreach (var mapping in mappings)
        {
            var key = Normalize(mapping.MessageType);
            if (_messages.TryGetValue(key, out var message))
            {
                _routes.Add((mapping.TopicPattern, message));

                if (OverlapsSparkplugNamespace(mapping.TopicPattern))
                {
                    var warning = $"WARN: topic pattern '{mapping.TopicPattern}' overlaps the Sparkplug B " +
                                  "namespace (spBv1.0). Sparkplug decoding takes precedence there; " +
                                  "the protobuf decoder will not claim those topics.";
                    _diagnostics.Add(warning);
                    logger?.LogWarning("protobuf schema: {Detail}", warning);
                }
            }
            else
            {
                _diagnostics.Add($"ERROR: message type '{mapping.MessageType}' not found for topic '{mapping.TopicPattern}'.");
                logger?.LogError("protobuf schema: message type {Type} not found for topic {Topic}",
                    mapping.MessageType, mapping.TopicPattern);
            }
        }
    }

    private void IndexMessage(string parentScope, DescriptorProto message, string schemaRoot, ILogger? logger)
    {
        var fqn = $"{parentScope}.{message.Name}";
        if (!_messages.TryAdd(fqn, message))
            WarnDuplicate("message", fqn, schemaRoot, logger);

        foreach (var nested in message.NestedTypes)
            IndexMessage(fqn, nested, schemaRoot, logger);
        foreach (var nestedEnum in message.EnumTypes)
            IndexEnum($"{fqn}.{nestedEnum.Name}", nestedEnum, schemaRoot, logger);
    }

    private void IndexEnum(string fqn, EnumDescriptorProto @enum, string schemaRoot, ILogger? logger)
    {
        if (!_enums.TryAdd(fqn, @enum))
            WarnDuplicate("enum", fqn, schemaRoot, logger);
    }

    // Two schema sets declaring the same fully-qualified name is legal but ambiguous:
    // routes bind to their own descriptor, while nested-type lookups resolve to whichever
    // was indexed first. Surface it rather than letting one silently win.
    private void WarnDuplicate(string kind, string fqn, string schemaRoot, ILogger? logger)
    {
        var warning = $"WARN: {kind} '{fqn}' from '{schemaRoot}' is already defined by another " +
                      "schema set; the first definition is used for nested-type resolution.";
        _diagnostics.Add(warning);
        logger?.LogWarning("protobuf schema: {Detail}", warning);
    }

    private static string Normalize(string fqn) =>
        fqn.StartsWith('.') ? fqn : "." + fqn;

    private static bool OverlapsSparkplugNamespace(string topicPattern)
    {
        var firstSegmentEnd = topicPattern.IndexOf('/');
        var firstSegment = firstSegmentEnd < 0 ? topicPattern : topicPattern[..firstSegmentEnd];

        return firstSegment is "#" or "+" ||
               string.Equals(firstSegment, "spBv1.0", StringComparison.Ordinal);
    }
}
