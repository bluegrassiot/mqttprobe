using MqttProbe.UI.Components.Layout;

namespace MqttProbe.UI.Components.Browser;

internal static class TopicTreeIconLanguage
{
    public const string BranchRole = "branch";
    public const string LeafRole = "leaf";
    public const string NamespaceRole = "namespace";
    public const string GroupRole = "group";
    public const string NodeRole = "node";
    public const string DeviceRole = "device";

    public static string LeafGlyph => LucideIcons.Circle;

    public static string MessageTypeGlyph => LucideIcons.Hash;

    public static string BranchGlyph(bool isExpanded) =>
        isExpanded ? LucideIcons.FolderOpen : LucideIcons.Folder;

    public static string NodeGlyph(TopicTreeRowModel row) =>
        KindGlyph(row.Kind, row.HasChildren, row.IsExpanded);

    // Role glyphs keep their shape whichever way the row is presented; only the plain
    // topic rows and the Devices container follow the folder/leaf split.
    public static string KindGlyph(TopicTreeRowKind kind, bool hasChildren, bool isExpanded) => kind switch
    {
        TopicTreeRowKind.SparkplugNamespace => LucideIcons.Zap,
        TopicTreeRowKind.SparkplugGroup => LucideIcons.Layers,
        TopicTreeRowKind.SparkplugNode => LucideIcons.Router,
        TopicTreeRowKind.SparkplugDevice => LucideIcons.Network,
        TopicTreeRowKind.MessageType => LucideIcons.Hash,
        TopicTreeRowKind.Spelling => LeafGlyph,
        TopicTreeRowKind.Devices => BranchGlyph(isExpanded),
        _ => hasChildren ? BranchGlyph(isExpanded) : LeafGlyph
    };

    public static string RoleGlyph(TopicTreeRowKind kind) =>
        KindGlyph(kind, hasChildren: true, isExpanded: false);

    // Shape says what a row is; colour says whose scope it belongs to. A node and every message
    // type grouped under it share one hue, a device and its message types share another, and an
    // ordinary topic is a blue branch or a green circle whatever its owner. Namespace, group and
    // the retired Devices container keep their own category hue.
    public static string IconClass(TopicTreeRowModel row) =>
        $"topic-node-icon topic-role--{RoleFor(row.Kind, row.Owner, row.HasChildren)}";

    public static string RoleClass(TopicTreeRowKind kind) =>
        $"topic-role--{RoleFor(kind, owner: null, hasChildren: true)}";

    private static string RoleFor(TopicTreeRowKind kind, TopicTreeOwner? owner, bool hasChildren) => kind switch
    {
        TopicTreeRowKind.SparkplugNamespace => NamespaceRole,
        TopicTreeRowKind.SparkplugGroup => GroupRole,
        TopicTreeRowKind.SparkplugNode => NodeRole,
        TopicTreeRowKind.SparkplugDevice or TopicTreeRowKind.Devices => DeviceRole,
        TopicTreeRowKind.MessageType => owner == TopicTreeOwner.Device ? DeviceRole : NodeRole,
        TopicTreeRowKind.Spelling => LeafRole,
        _ => hasChildren ? BranchRole : LeafRole
    };
}
