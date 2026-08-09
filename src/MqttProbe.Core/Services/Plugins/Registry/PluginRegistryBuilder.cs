using System.Collections.ObjectModel;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Plugins.Registry;

public sealed class PluginRegistryBuilder : IPluginRegistrationContext
{
    public const string BuiltInPluginId = "__built_in__";

    private readonly List<CapabilityRegistration<IPayloadDetector>> _detectors = [];
    private readonly List<CapabilityRegistration<IPayloadDecoder>> _decoders = [];
    private readonly List<CapabilityRegistration<ITopologyExtractor>> _topologyExtractors = [];
    private readonly List<CapabilityRegistration<IPayloadEncoder>> _encoders = [];
    private readonly List<CapabilityRegistration<IPayloadTemplateProvider>> _templateProviders = [];
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

    public void RegisterDetector(IPayloadDetector detector) =>
        Add(_detectors, detector, detector.FormatId);

    public void RegisterDecoder(IPayloadDecoder decoder) =>
        Add(_decoders, decoder, decoder.FormatId);

    public void RegisterTopologyExtractor(ITopologyExtractor extractor) =>
        Add(_topologyExtractors, extractor, extractor.FormatId);

    public void RegisterEncoder(IPayloadEncoder encoder) =>
        Add(_encoders, encoder, encoder.FormatId);

    public void RegisterTemplateProvider(IPayloadTemplateProvider provider) =>
        Add(_templateProviders, provider, provider.FormatId);

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
            ReportDuplicatePluginId(pluginId);
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
            ReportDuplicatePluginId(pluginId);
            _duplicateIdActive = true;
            return;
        }

        _currentPluginId = pluginId;
        _duplicateIdActive = false;
    }

    public PluginRegistry Build(
        IReadOnlyCollection<string>? disabledPluginIds = null,
        IReadOnlyCollection<PluginOverrideConfig>? overrides = null)
    {
        var disabled = disabledPluginIds is { Count: > 0 }
            ? new HashSet<string>(disabledPluginIds, StringComparer.OrdinalIgnoreCase)
            : [];

        ReportDisabledPlugins(disabled);

        var overrideMap = BuildOverrideMap(overrides);
        var selector = new CapabilitySelector(_diagnostics);

        // Diagnostics accumulate as each capability is selected, so the snapshot below has to
        // come last.
        var detectors = SelectDetectors(selector, disabled, overrideMap);
        var decoders = SelectMap(selector, "Decoder", _decoders, disabled, overrideMap);
        var topologyExtractors = SelectMap(selector, "TopologyExtractor", _topologyExtractors, disabled, overrideMap);
        var encoders = SelectMap(selector, "Encoder", _encoders, disabled, overrideMap);
        var templateProviders = SelectMap(selector, "TemplateProvider", _templateProviders, disabled, overrideMap);

        return new PluginRegistry(
            detectors,
            decoders,
            topologyExtractors,
            encoders,
            templateProviders,
            _diagnostics.ToList().AsReadOnly(),
            new HashSet<string>(_loadedPackagePaths, StringComparer.OrdinalIgnoreCase));
    }

    private void Add<T>(List<CapabilityRegistration<T>> registrations, T item, string formatId)
    {
        if (_duplicateIdActive)
        {
            return;
        }

        registrations.Add(new CapabilityRegistration<T>(item, formatId, _currentPluginId, _insertionCounter++));
    }

    private void RollbackRegistrations(int counterSnapshot)
    {
        _detectors.RemoveAll(e => e.InsertionOrder >= counterSnapshot);
        _decoders.RemoveAll(e => e.InsertionOrder >= counterSnapshot);
        _topologyExtractors.RemoveAll(e => e.InsertionOrder >= counterSnapshot);
        _encoders.RemoveAll(e => e.InsertionOrder >= counterSnapshot);
        _templateProviders.RemoveAll(e => e.InsertionOrder >= counterSnapshot);
    }

    private void ReportDuplicatePluginId(string pluginId) =>
        _diagnostics.Add(new PluginDiagnosticEntry
        {
            Source = pluginId,
            Severity = DiagnosticSeverity.Error,
            Message = $"Duplicate plugin ID '{pluginId}'; second registration ignored."
        });

    private void ReportDisabledPlugins(HashSet<string> disabled)
    {
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
    }

    private static Dictionary<CapabilityOverrideKey, string> BuildOverrideMap(
        IReadOnlyCollection<PluginOverrideConfig>? overrides)
    {
        var map = new Dictionary<CapabilityOverrideKey, string>();

        foreach (var o in overrides ?? [])
        {
            map[new CapabilityOverrideKey(o.Capability, o.FormatId)] = o.PluginId;
        }

        return map;
    }

    private ReadOnlyCollection<IPayloadDetector> SelectDetectors(
        CapabilitySelector selector,
        HashSet<string> disabled,
        Dictionary<CapabilityOverrideKey, string> overrides)
    {
        var winners = selector.SelectWinners("Detector", _detectors, disabled, overrides);

        winners.Sort((a, b) =>
        {
            var cmp = b.Item.Priority.CompareTo(a.Item.Priority);
            return cmp != 0 ? cmp : a.InsertionOrder.CompareTo(b.InsertionOrder);
        });

        return winners.Select(w => w.Item).ToList().AsReadOnly();
    }

    private static ReadOnlyDictionary<string, T> SelectMap<T>(
        CapabilitySelector selector,
        string capability,
        List<CapabilityRegistration<T>> registrations,
        HashSet<string> disabled,
        Dictionary<CapabilityOverrideKey, string> overrides)
        where T : class
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);

        foreach (var winner in selector.SelectWinners(capability, registrations, disabled, overrides))
        {
            map[winner.FormatId] = winner.Item;
        }

        return map.AsReadOnly();
    }
}
