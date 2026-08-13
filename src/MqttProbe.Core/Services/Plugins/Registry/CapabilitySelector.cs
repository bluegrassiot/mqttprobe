namespace MqttProbe.Core.Services.Plugins.Registry;

internal sealed record CapabilityRegistration<T>(T Item, string FormatId, string PluginId, int InsertionOrder);

internal readonly record struct CapabilityOverrideKey(string Capability, string FormatId);

// Picks the single winner per format id when more than one plugin claims it, and records why
// each loser was dropped.
internal sealed class CapabilitySelector(List<PluginDiagnosticEntry> diagnostics)
{
    public List<CapabilityRegistration<T>> SelectWinners<T>(
        string capability,
        List<CapabilityRegistration<T>> registrations,
        HashSet<string> disabled,
        Dictionary<CapabilityOverrideKey, string> overrides)
    {
        var winners = new List<CapabilityRegistration<T>>();

        foreach (var (formatId, candidates) in GroupByFormatId(registrations, disabled))
        {
            WarnIfOverrideTargetDisabled(capability, formatId, registrations, disabled, overrides);

            var winner = candidates.Count == 1
                ? 0
                : ResolveOverrideWinner(capability, formatId, candidates, disabled, overrides)
                  ?? ResolveDefaultWinner(capability, formatId, candidates);

            winners.Add(candidates[winner]);
        }

        return winners;
    }

    private static List<(string FormatId, List<CapabilityRegistration<T>> Candidates)> GroupByFormatId<T>(
        List<CapabilityRegistration<T>> registrations,
        HashSet<string> disabled)
    {
        var groups = new Dictionary<string, List<CapabilityRegistration<T>>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var registration in registrations)
        {
            if (disabled.Contains(registration.PluginId))
            {
                continue;
            }

            if (!groups.TryGetValue(registration.FormatId, out var candidates))
            {
                candidates = [];
                groups[registration.FormatId] = candidates;
                order.Add(registration.FormatId);
            }

            candidates.Add(registration);
        }

        return order.Select(formatId => (formatId, groups[formatId])).ToList();
    }

    // A disabled override target is filtered out before grouping, so a format left with one
    // candidate would otherwise apply default precedence without ever saying the override was ignored.
    private void WarnIfOverrideTargetDisabled<T>(
        string capability,
        string formatId,
        List<CapabilityRegistration<T>> registrations,
        HashSet<string> disabled,
        Dictionary<CapabilityOverrideKey, string> overrides)
    {
        if (!overrides.TryGetValue(new CapabilityOverrideKey(capability, formatId), out var overridePluginId)
            || !disabled.Contains(overridePluginId))
        {
            return;
        }

        // overridePluginId comes from hand written config; matched the same way as DisabledPluginIds.
        var hadRegistered = registrations.Exists(r =>
            r.FormatId == formatId
            && string.Equals(r.PluginId, overridePluginId, StringComparison.OrdinalIgnoreCase));

        if (hadRegistered)
        {
            diagnostics.Add(DisabledOverrideTargetWarning(capability, formatId, overridePluginId));
        }
    }

    private int? ResolveOverrideWinner<T>(
        string capability,
        string formatId,
        List<CapabilityRegistration<T>> candidates,
        HashSet<string> disabled,
        Dictionary<CapabilityOverrideKey, string> overrides)
    {
        if (!overrides.TryGetValue(new CapabilityOverrideKey(capability, formatId), out var overridePluginId))
        {
            return null;
        }

        var winner = candidates.FindIndex(c =>
            string.Equals(c.PluginId, overridePluginId, StringComparison.OrdinalIgnoreCase));

        if (winner >= 0)
        {
            ReportLosers(candidates, winner, DiagnosticSeverity.Info,
                $"{capability} for '{formatId}' overridden by plugin '{overridePluginId}'.");
            return winner;
        }

        diagnostics.Add(disabled.Contains(overridePluginId)
            ? DisabledOverrideTargetWarning(capability, formatId, overridePluginId)
            : new PluginDiagnosticEntry
            {
                Source = capability,
                Severity = DiagnosticSeverity.Warning,
                Message = $"{capability} override for '{formatId}' targets plugin '{overridePluginId}' which did not register; default precedence applies."
            });

        return null;
    }

    private int ResolveDefaultWinner<T>(
        string capability,
        string formatId,
        List<CapabilityRegistration<T>> candidates)
    {
        var builtIn = candidates.FindIndex(c => c.PluginId == PluginRegistryBuilder.BuiltInPluginId);

        if (builtIn >= 0)
        {
            ReportLosers(candidates, builtIn, DiagnosticSeverity.Warning,
                $"{capability} for '{formatId}' disabled: built-in takes precedence.");
            return builtIn;
        }

        ReportLosers(candidates, 0, DiagnosticSeverity.Warning,
            $"{capability} for '{formatId}' disabled: already registered by '{candidates[0].PluginId}'.");
        return 0;
    }

    private void ReportLosers<T>(
        List<CapabilityRegistration<T>> candidates,
        int winner,
        DiagnosticSeverity severity,
        string message)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (i == winner)
            {
                continue;
            }

            diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = candidates[i].PluginId,
                Severity = severity,
                Message = message
            });
        }
    }

    private static PluginDiagnosticEntry DisabledOverrideTargetWarning(
        string capability,
        string formatId,
        string overridePluginId) =>
        new()
        {
            Source = capability,
            Severity = DiagnosticSeverity.Warning,
            Message = $"{capability} override for '{formatId}' targets plugin '{overridePluginId}' which is disabled; default precedence applies."
        };
}
