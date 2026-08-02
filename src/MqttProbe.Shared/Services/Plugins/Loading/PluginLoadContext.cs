using System.Reflection;
using System.Runtime.Loader;

namespace MqttProbe.Services.Plugins.Loading;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    // Assemblies that must always resolve from the default context to preserve
    // type identity.  A plugin directory may contain its own copy of these
    // binaries, but the host already loaded them and plugins must share the
    // same types.
    private static readonly HashSet<string> _sharedAssemblyNames =
        new(StringComparer.Ordinal) { "MqttProbe.Shared" };

    internal static IReadOnlyCollection<string> SharedAssemblyNames => _sharedAssemblyNames;

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    internal static bool IsSharedAssembly(string? assemblyName) =>
        assemblyName is not null && _sharedAssemblyNames.Contains(assemblyName);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedAssembly(assemblyName.Name))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
