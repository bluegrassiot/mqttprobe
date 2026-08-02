using System.Reflection;
using System.Runtime.InteropServices;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.Packaging;

public static class PluginAssemblyInspector
{
    public static bool AssemblyPluginsSupported =>
        !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    // Reflection-only. Nothing from the uploaded package executes here, and the
    // context is disposed before publish so the staged DLL is never left locked.
    public static PluginValidationResult Validate(string extractPath, string id)
    {
        if (!AssemblyPluginsSupported)
        {
            return PluginValidationResult.Fail(
                "Binary plugin packages are not supported on this platform.");
        }

        var primaryPath = Path.Combine(extractPath, id + ".dll");

        if (!File.Exists(primaryPath))
        {
            return PluginValidationResult.Fail(
                $"Assembly package must contain a primary assembly named {id}.dll at its root.");
        }

        // IL3000: the empty-string single-file case is exactly what the next check handles.
#pragma warning disable IL3000
        var contractPath = typeof(IMqttProbePlugin).Assembly.Location;
#pragma warning restore IL3000

        if (string.IsNullOrEmpty(contractPath))
        {
            return PluginValidationResult.Fail(
                "Assembly packages cannot be validated in a single-file build of MQTTProbe.");
        }

        var searchPaths = BuildSearchPaths(extractPath, contractPath);

        return ValidateAgainstContract(primaryPath, contractPath, searchPaths, id);
    }

    // Walking a candidate's types resolves its entire dependency graph (Google.Protobuf, the
    // ASP.NET Core shared framework, ...), so the core runtime directory alone is not enough.
    // TRUSTED_PLATFORM_ASSEMBLIES is the host's own resolved list, delimited by
    // Path.PathSeparator -- ';' on Windows but ':' everywhere else. Splitting on a
    // literal ';' collapsed the whole list into one bogus path on Linux and macOS,
    // so every plugin dependency failed to resolve there.
    private static string[] BuildSearchPaths(string extractPath, string contractPath)
    {
        var platformAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

        return Directory
            .GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            .Concat(platformAssemblies)
            .Concat(Directory.GetFiles(extractPath, "*.dll", SearchOption.AllDirectories))
            .Append(contractPath)
            .DistinctBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PluginValidationResult ValidateAgainstContract(
        string primaryPath, string contractPath, string[] searchPaths, string id)
    {
        try
        {
            using var context = new MetadataLoadContext(new PathAssemblyResolver(searchPaths));

            var contractType = context
                .LoadFromAssemblyPath(contractPath)
                .GetType(typeof(IMqttProbePlugin).FullName!);

            if (contractType is null)
            {
                return PluginValidationResult.Fail(
                    "Could not resolve the IMqttProbePlugin contract for validation.");
            }

            var candidate = context.LoadFromAssemblyPath(primaryPath);

            var hasPlugin = candidate
                .GetTypes()
                .Any(type => type is { IsAbstract: false, IsInterface: false }
                    && contractType.IsAssignableFrom(type));

            return hasPlugin
                ? PluginValidationResult.Success
                : PluginValidationResult.Fail(
                    $"{id}.dll does not contain a public type implementing IMqttProbePlugin.");
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
        {
            return PluginValidationResult.Fail($"{id}.dll is not a valid .NET assembly: {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            return PluginValidationResult.Fail(
                $"{id}.dll depends on an assembly that could not be resolved for validation: {ex.Message}");
        }
        catch (ReflectionTypeLoadException ex)
        {
            return PluginValidationResult.Fail(
                $"{id}.dll references types that could not be resolved: {ex.Message}");
        }
    }
}
