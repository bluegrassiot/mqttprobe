using System.Collections.ObjectModel;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.Registry;

public sealed class PluginRegistry
{
    public IReadOnlyList<IPayloadDetector> Detectors { get; }
    public IReadOnlyDictionary<string, IPayloadDecoder> Decoders { get; }
    public IReadOnlyDictionary<string, ITopologyExtractor> TopologyExtractors { get; }
    public IReadOnlyDictionary<string, IPayloadEncoder> Encoders { get; }
    public IReadOnlyDictionary<string, IPayloadTemplateProvider> TemplateProviders { get; }
    public IReadOnlyList<PluginDiagnosticEntry> Diagnostics { get; }
    public IReadOnlySet<string> LoadedPackagePaths { get; }

    internal PluginRegistry(
        IReadOnlyList<IPayloadDetector> detectors,
        IReadOnlyDictionary<string, IPayloadDecoder> decoders,
        IReadOnlyDictionary<string, ITopologyExtractor> topologyExtractors,
        IReadOnlyDictionary<string, IPayloadEncoder> encoders,
        IReadOnlyDictionary<string, IPayloadTemplateProvider> templateProviders,
        IReadOnlyList<PluginDiagnosticEntry> diagnostics,
        IReadOnlySet<string> loadedPackagePaths)
    {
        Detectors = detectors;
        Decoders = decoders;
        TopologyExtractors = topologyExtractors;
        Encoders = encoders;
        TemplateProviders = templateProviders;
        Diagnostics = diagnostics;
        LoadedPackagePaths = loadedPackagePaths;
    }

    public IPayloadDetector? FindDetector(MqttApplicationMessageReceivedEventArgs e) =>
        Detectors.FirstOrDefault(detector => detector.CanDetect(e));
    public IEnumerable<IPayloadDetector> FindMatchingDetectors(MqttApplicationMessageReceivedEventArgs e) =>
        Detectors.Where(detector => detector.CanDetect(e));
    public IPayloadDecoder? FindDecoder(string formatId) =>
        Decoders.TryGetValue(formatId, out var decoder) ? decoder : null;
    public IPayloadEncoder? FindEncoder(string formatId) =>
        Encoders.TryGetValue(formatId, out var encoder) ? encoder : null;
    public ITopologyExtractor? FindTopologyExtractor(string formatId) =>
        TopologyExtractors.TryGetValue(formatId, out var extractor) ? extractor : null;
}

public sealed class PluginRegistryBuilder : IPluginRegistrationContext
{
    public const string BuiltInPluginId = "__built_in__";

    private readonly List<(IPayloadDetector Detector, string PluginId, int InsertionOrder)> _detectors = [];
    private readonly List<(IPayloadDecoder Decoder, string PluginId, int InsertionOrder)> _decoders = [];
    private readonly List<(ITopologyExtractor Extractor, string PluginId, int InsertionOrder)> _topologyExtractors = [];
    private readonly List<(IPayloadEncoder Encoder, string PluginId, int InsertionOrder)> _encoders = [];
    private readonly List<(IPayloadTemplateProvider Provider, string PluginId, int InsertionOrder)> _templateProviders = [];
    private readonly List<PluginDiagnosticEntry> _diagnostics = [];
    private readonly HashSet<string> _registeredPluginIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedPackagePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _installedDisabledIds = new(StringComparer.OrdinalIgnoreCase);

    private string _currentPluginId = BuiltInPluginId;
    private bool _duplicateIdActive;
    private int _insertionCounter;

    public void AddDiagnostic(PluginDiagnosticEntry entry) => _diagnostics.Add(entry);

    public void RegisterPackagePath(string path) => _loadedPackagePaths.Add(Path.GetFullPath(path));

    // The loader drops disabled plugins before they ever register, so Build() cannot tell a
    // correctly-disabled plugin from an id that matches nothing. This carries that fact across.
    public void NoteInstalledDisabledPlugin(string pluginId) => _installedDisabledIds.Add(pluginId);

    public void RegisterPlugin(string pluginId, Action<IPluginRegistrationContext> configure)
    {
        var previousPluginId = _currentPluginId;
        var previousDuplicateState = _duplicateIdActive;
        var isNewId = _registeredPluginIds.Add(pluginId);

        if (isNewId)
        {
            _currentPluginId = pluginId;
            _duplicateIdActive = false;
        }
        else
        {
            _diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = pluginId,
                Severity = DiagnosticSeverity.Error,
                Message = $"Duplicate plugin ID '{pluginId}'; second registration ignored."
            });
            _duplicateIdActive = true;
        }

        var counterSnapshot = _insertionCounter;

        try
        {
            configure(this);
        }
        catch
        {
            if (isNewId)
            {
                _registeredPluginIds.Remove(pluginId);
            }

            RollbackRegistrations(counterSnapshot);

            throw;
        }
        finally
        {
            _currentPluginId = previousPluginId;
            _duplicateIdActive = previousDuplicateState;
        }
    }

    public void SetCurrentPluginId(string pluginId)
    {
        if (!_registeredPluginIds.Add(pluginId))
        {
            _diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = pluginId,
                Severity = DiagnosticSeverity.Error,
                Message = $"Duplicate plugin ID '{pluginId}'; second registration ignored."
            });
            _duplicateIdActive = true;
            return;
        }

        _currentPluginId = pluginId;
        _duplicateIdActive = false;
    }

    public void RegisterDetector(IPayloadDetector detector)
    {
        if (_duplicateIdActive)
        {
            return;
        }

        _detectors.Add((detector, _currentPluginId, _insertionCounter++));
    }

    public void RegisterDecoder(IPayloadDecoder decoder)
    {
        if (_duplicateIdActive)
        {
            return;
        }

        _decoders.Add((decoder, _currentPluginId, _insertionCounter++));
    }

    public void RegisterTopologyExtractor(ITopologyExtractor extractor)
    {
        if (_duplicateIdActive)
        {
            return;
        }

        _topologyExtractors.Add((extractor, _currentPluginId, _insertionCounter++));
    }

    public void RegisterEncoder(IPayloadEncoder encoder)
    {
        if (_duplicateIdActive)
        {
            return;
        }

        _encoders.Add((encoder, _currentPluginId, _insertionCounter++));
    }

    public void RegisterTemplateProvider(IPayloadTemplateProvider provider)
    {
        if (_duplicateIdActive)
        {
            return;
        }

        _templateProviders.Add((provider, _currentPluginId, _insertionCounter++));
    }

    public PluginRegistry Build(
        IReadOnlyCollection<string>? disabledPluginIds = null,
        IReadOnlyCollection<PluginOverrideConfig>? overrides = null)
    {
        var disabled = disabledPluginIds is { Count: > 0 }
            ? new HashSet<string>(disabledPluginIds, StringComparer.OrdinalIgnoreCase)
            : [];

        foreach (var id in disabled)
        {
            // An id matching nothing is a typo, and reporting it as "registrations skipped"
            // confirmed an edit that had in fact done nothing at all.
            var installed = _registeredPluginIds.Contains(id) || _installedDisabledIds.Contains(id);

            _diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = id,
                Severity = installed ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning,
                Message = installed
                    ? $"Plugin '{id}' is disabled; all registrations skipped."
                    : $"Plugin '{id}' is listed in DisabledPluginIds but no installed plugin has that ID; the entry has no effect."
            });
        }

        var overrideMap = BuildOverrideMap(overrides);

        var detectors = BuildDetectors(disabled, overrideMap);
        var decoders = BuildDecoders(disabled, overrideMap);
        var topologyExtractors = BuildTopologyExtractors(disabled, overrideMap);
        var encoders = BuildEncoders(disabled, overrideMap);
        var templateProviders = BuildTemplateProviders(disabled, overrideMap);

        return new PluginRegistry(
            detectors,
            decoders,
            topologyExtractors,
            encoders,
            templateProviders,
            _diagnostics.ToList().AsReadOnly(),
            new HashSet<string>(_loadedPackagePaths, StringComparer.OrdinalIgnoreCase));
    }

    private static Dictionary<(string Capability, string FormatId), string> BuildOverrideMap(
        IReadOnlyCollection<PluginOverrideConfig>? overrides)
    {
        var map = new Dictionary<(string, string), string>();
        if (overrides is null)
        {
            return map;
        }

        foreach (var o in overrides)
        {
            map[(o.Capability, o.FormatId)] = o.PluginId;
        }

        return map;
    }

    private ReadOnlyCollection<IPayloadDetector> BuildDetectors(
        HashSet<string> disabled,
        Dictionary<(string, string), string> overrideMap)
    {
        var allEntries = _detectors;
        var groups = GroupByFormatId(
            allEntries, disabled, d => d.Detector.FormatId, d => d.PluginId);

        var selected = new List<(IPayloadDetector Detector, string PluginId, int InsertionOrder)>();

        foreach (var (formatId, entries) in groups)
        {
            CheckDisabledOverrideTarget(formatId, allEntries, disabled, overrideMap, "Detector",
                d => d.Detector.FormatId, d => d.PluginId);

            var winner = ResolveGroupConflict(
                formatId, entries, disabled, overrideMap, "Detector",
                e => e.PluginId);

            if (winner.HasValue)
            {
                selected.Add(entries[winner.Value]);
            }
        }

        selected.Sort((a, b) =>
        {
            var cmp = b.Detector.Priority.CompareTo(a.Detector.Priority);
            return cmp != 0 ? cmp : a.InsertionOrder.CompareTo(b.InsertionOrder);
        });

        return selected.Select(d => d.Detector).ToList().AsReadOnly();
    }

    private ReadOnlyDictionary<string, IPayloadDecoder> BuildDecoders(
        HashSet<string> disabled,
        Dictionary<(string, string), string> overrideMap) =>
        BuildUniqueCapabilityMap(
            _decoders, disabled, overrideMap, "Decoder",
            d => d.Decoder, d => d.Decoder.FormatId, d => d.PluginId);

    private ReadOnlyDictionary<string, IPayloadEncoder> BuildEncoders(
        HashSet<string> disabled,
        Dictionary<(string, string), string> overrideMap) =>
        BuildUniqueCapabilityMap(
            _encoders, disabled, overrideMap, "Encoder",
            e => e.Encoder, e => e.Encoder.FormatId, e => e.PluginId);

    private ReadOnlyDictionary<string, ITopologyExtractor> BuildTopologyExtractors(
        HashSet<string> disabled,
        Dictionary<(string, string), string> overrideMap) =>
        BuildUniqueCapabilityMap(
            _topologyExtractors, disabled, overrideMap, "TopologyExtractor",
            t => t.Extractor, t => t.Extractor.FormatId, t => t.PluginId);

    private ReadOnlyDictionary<string, IPayloadTemplateProvider> BuildTemplateProviders(
        HashSet<string> disabled,
        Dictionary<(string, string), string> overrideMap) =>
        BuildUniqueCapabilityMap(
            _templateProviders, disabled, overrideMap, "TemplateProvider",
            p => p.Provider, p => p.Provider.FormatId, p => p.PluginId);

    private ReadOnlyDictionary<string, TCapability> BuildUniqueCapabilityMap<TTuple, TCapability>(
        List<TTuple> entries,
        HashSet<string> disabled,
        Dictionary<(string, string), string> overrideMap,
        string capability,
        Func<TTuple, TCapability> getItem,
        Func<TTuple, string> getFormatId,
        Func<TTuple, string> getPluginId)
        where TCapability : class
    {
        var groups = GroupByFormatId(entries, disabled, getFormatId, getPluginId);

        var result = new Dictionary<string, TCapability>(StringComparer.Ordinal);

        foreach (var (formatId, group) in groups)
        {
            CheckDisabledOverrideTarget(formatId, entries, disabled, overrideMap, capability,
                getFormatId, getPluginId);

            var winner = ResolveGroupConflict(
                formatId, group, disabled, overrideMap, capability,
                getPluginId);

            if (winner.HasValue)
            {
                result[formatId] = getItem(group[winner.Value]);
            }
        }

        return result.AsReadOnly();
    }

    private void CheckDisabledOverrideTarget<TTuple>(
        string formatId,
        List<TTuple> allEntries,
        HashSet<string> disabled,
        Dictionary<(string, string), string> overrideMap,
        string capability,
        Func<TTuple, string> getFormatId,
        Func<TTuple, string> getPluginId)
    {
        if (!overrideMap.TryGetValue((capability, formatId), out var overridePluginId))
        {
            return;
        }

        if (!disabled.Contains(overridePluginId))
        {
            return;
        }

        // overridePluginId comes from hand-written config; matched the same way as DisabledPluginIds.
        var hadRegistered = allEntries.Exists(e =>
            getFormatId(e) == formatId
            && string.Equals(getPluginId(e), overridePluginId, StringComparison.OrdinalIgnoreCase));

        if (hadRegistered)
        {
            _diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = capability,
                Severity = DiagnosticSeverity.Warning,
                Message = $"{capability} override for '{formatId}' targets plugin '{overridePluginId}' which is disabled; default precedence applies."
            });
        }
    }

    private void RollbackRegistrations(int counterSnapshot)
    {
        _detectors.RemoveAll(e => e.InsertionOrder >= counterSnapshot);
        _decoders.RemoveAll(e => e.InsertionOrder >= counterSnapshot);
        _topologyExtractors.RemoveAll(e => e.InsertionOrder >= counterSnapshot);
        _encoders.RemoveAll(e => e.InsertionOrder >= counterSnapshot);
        _templateProviders.RemoveAll(e => e.InsertionOrder >= counterSnapshot);
    }

    private static List<(string FormatId, List<TTuple> Entries)> GroupByFormatId<TTuple>(
        List<TTuple> entries,
        HashSet<string> disabled,
        Func<TTuple, string> getFormatId,
        Func<TTuple, string> getPluginId)
    {
        var groups = new Dictionary<string, List<TTuple>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var entry in entries)
        {
            if (disabled.Contains(getPluginId(entry)))
            {
                continue;
            }

            var formatId = getFormatId(entry);

            if (!groups.TryGetValue(formatId, out var list))
            {
                list = [];
                groups[formatId] = list;
                order.Add(formatId);
            }

            list.Add(entry);
        }

        return order.Select(id => (id, groups[id])).ToList();
    }

    private int? ResolveGroupConflict<TTuple>(
        string formatId,
        List<TTuple> entries,
        HashSet<string> disabled,
        Dictionary<(string, string), string> overrideMap,
        string capability,
        Func<TTuple, string> getPluginId)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        if (entries.Count == 1)
        {
            return 0;
        }

        var overrideWinner = TryResolveOverrideWinner(
            formatId, entries, disabled, overrideMap, capability, getPluginId);

        if (overrideWinner.HasValue)
        {
            return overrideWinner;
        }

        return ResolveDefaultWinner(formatId, entries, capability, getPluginId);
    }

    private int? TryResolveOverrideWinner<TTuple>(
        string formatId,
        List<TTuple> entries,
        HashSet<string> disabled,
        Dictionary<(string, string), string> overrideMap,
        string capability,
        Func<TTuple, string> getPluginId)
    {
        if (!overrideMap.TryGetValue((capability, formatId), out var overridePluginId))
        {
            return null;
        }

        var overrideIndex = entries.FindIndex(e =>
            string.Equals(getPluginId(e), overridePluginId, StringComparison.OrdinalIgnoreCase));

        if (overrideIndex >= 0)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (i == overrideIndex)
                {
                    continue;
                }

                _diagnostics.Add(new PluginDiagnosticEntry
                {
                    Source = getPluginId(entries[i]),
                    Severity = DiagnosticSeverity.Info,
                    Message = $"{capability} for '{formatId}' overridden by plugin '{overridePluginId}'."
                });
            }

            return overrideIndex;
        }

        var isDisabled = disabled.Contains(overridePluginId);

        _diagnostics.Add(new PluginDiagnosticEntry
        {
            Source = capability,
            Severity = DiagnosticSeverity.Warning,
            Message = isDisabled
                ? $"{capability} override for '{formatId}' targets plugin '{overridePluginId}' which is disabled; default precedence applies."
                : $"{capability} override for '{formatId}' targets plugin '{overridePluginId}' which did not register; default precedence applies."
        });

        return null;
    }

    private int ResolveDefaultWinner<TTuple>(
        string formatId,
        List<TTuple> entries,
        string capability,
        Func<TTuple, string> getPluginId)
    {
        var builtInIndex = entries.FindIndex(e => getPluginId(e) == BuiltInPluginId);

        if (builtInIndex >= 0)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (i == builtInIndex)
                {
                    continue;
                }

                _diagnostics.Add(new PluginDiagnosticEntry
                {
                    Source = getPluginId(entries[i]),
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"{capability} for '{formatId}' disabled: built-in takes precedence."
                });
            }

            return builtInIndex;
        }

        for (var i = 1; i < entries.Count; i++)
        {
            _diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = getPluginId(entries[i]),
                Severity = DiagnosticSeverity.Warning,
                Message = $"{capability} for '{formatId}' disabled: already registered by '{getPluginId(entries[0])}'."
            });
        }

        return 0;
    }
}
