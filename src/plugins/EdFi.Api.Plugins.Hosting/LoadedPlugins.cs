// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Every plugin the loader returned, in allowlist order.
/// </summary>
/// <remarks>
/// An aggregate rather than a bare list because the composition phases are added to it later: the
/// service contribution phase by the story that invokes hooks, and the configuration phase by the
/// secrets foundation story. Both need somewhere to live that a caller already holds.
/// </remarks>
public sealed class LoadedPlugins
{
    /// <summary>
    /// Creates an aggregate over the given plugins, in allowlist order. Internal because the loader is
    /// the only thing that has plugins to put in one.
    /// </summary>
    internal LoadedPlugins(IReadOnlyList<LoadedPlugin> plugins)
    {
        Plugins = plugins;
    }

    /// <summary>
    /// The value returned when no plugin was asked for, which is the shipped default.
    /// </summary>
    public static LoadedPlugins Empty { get; } = new([]);

    /// <summary>
    /// The loaded plugins, in the order the operator wrote them, which is the invocation order for
    /// both composition phases.
    /// </summary>
    public IReadOnlyList<LoadedPlugin> Plugins { get; }

    /// <summary>
    /// Invokes every plugin's service contribution hook, in allowlist order, and returns what each one
    /// contributed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each hook is handed a <see cref="RecordingServiceCollection"/> over the real collection, so a
    /// removal it is not allowed to make is refused before it lands. What it contributed is worked out
    /// by comparing the real collection either side of the call, which is exact because hooks run one
    /// at a time.
    /// </para>
    /// <para>
    /// The return value is handed back rather than registered. The host owns that decision: it
    /// registers this instance after every hook has run, which is what stops a plugin's own
    /// registration of the same type from winning.
    /// </para>
    /// <para>
    /// A failure throws rather than choosing a bootstrap phase to blame. This is called from inside the
    /// host's own service-registration code, so the failure lands in whichever phase the host was
    /// running.
    /// </para>
    /// </remarks>
    /// <exception cref="PluginCompositionException">
    /// A hook removed something it may not remove, cleared the collection, or threw.
    /// </exception>
    public PluginAuditInput ContributeServices(
        IServiceCollection services,
        IConfiguration configuration,
        PluginContractRegistry registry
    ) => ContributeServices(services, configuration, registry, Console.Error);

    /// <summary>
    /// The overload the public one calls with <see cref="Console.Error"/>, so a test can read the
    /// channel without redirecting the process's own.
    /// </summary>
    internal PluginAuditInput ContributeServices(
        IServiceCollection services,
        IConfiguration configuration,
        PluginContractRegistry registry,
        TextWriter diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(diagnostics);

        List<PluginContributionRecord> records = [];

        foreach (LoadedPlugin plugin in Plugins)
        {
            // Written before the call rather than after it. A plugin blocking inside a hook hangs
            // startup with no timeout, so the last line on the channel has to name the plugin that was
            // entered; a line written only on the way out is worth nothing for that.
            diagnostics.WriteLine(
                $"invoking ContributeServices on {PluginDiagnosticText.Quote(plugin.Name)}"
            );

            List<ServiceDescriptor> before = [.. services];

            Invoke(plugin, services, configuration, diagnostics);

            records.Add(RecordOf(plugin, before, services));
        }

        return new PluginAuditInput(registry, records, [.. services]);
    }

    private static void Invoke(
        LoadedPlugin plugin,
        IServiceCollection services,
        IConfiguration configuration,
        TextWriter diagnostics
    )
    {
        RecordingServiceCollection wrapper = new(services, plugin.Name, diagnostics);

        try
        {
            plugin.Instance.ContributeServices(wrapper, configuration);
        }
        catch (PluginCompositionException)
        {
            // Already the named fatal, already reported by the wrapper that raised it. Wrapping it
            // again would bury the rule that fired under a generic "the hook threw".
            throw;
        }
        catch (Exception exception)
        {
            string message =
                $"plugin '{PluginDiagnosticText.Quote(plugin.Name)}' threw from ContributeServices, the "
                + $"service composition phase: {PluginDiagnosticText.Quote(exception.GetType().FullName)}: "
                + PluginDiagnosticText.Quote(exception.Message);

            diagnostics.WriteLine($"plugin composition refused: {message}");

            throw new PluginCompositionException(
                PluginCompositionFailure.ContributeServicesThrew,
                plugin.Name,
                message,
                exception
            );
        }
    }

    /// <summary>
    /// Compares the collection either side of one hook, by reference and preserving multiplicity.
    /// </summary>
    /// <remarks>
    /// Occurrences rather than distinct descriptors, because a plugin that adds the same descriptor
    /// instance twice has registered two of them and a cardinality check counts what it registered.
    /// Walking the two sequences in order rather than enumerating a dictionary keeps the results in
    /// collection order, so a message listing them reads the way the collection does.
    /// </remarks>
    private static PluginContributionRecord RecordOf(
        LoadedPlugin plugin,
        List<ServiceDescriptor> before,
        IServiceCollection after
    )
    {
        List<ServiceDescriptor> additions = [];
        Dictionary<ServiceDescriptor, int> remainingBefore = OccurrencesOf(before);

        foreach (ServiceDescriptor descriptor in after)
        {
            if (remainingBefore.TryGetValue(descriptor, out int remaining) && remaining > 0)
            {
                remainingBefore[descriptor] = remaining - 1;
            }
            else
            {
                additions.Add(descriptor);
            }
        }

        List<PluginDescriptorDisplacement> removals = [];
        Dictionary<ServiceDescriptor, int> remainingAfter = OccurrencesOf(after);

        foreach (ServiceDescriptor descriptor in before)
        {
            if (remainingAfter.TryGetValue(descriptor, out int remaining) && remaining > 0)
            {
                remainingAfter[descriptor] = remaining - 1;
            }
            else
            {
                removals.Add(
                    new PluginDescriptorDisplacement(
                        descriptor.ServiceType,
                        ImplementationTypeOf(descriptor),
                        descriptor
                    )
                );
            }
        }

        HashSet<Type> removedServiceTypes = [.. removals.Select(removal => removal.ServiceType)];

        // A service type this plugin both added and removed a descriptor for. Distinct preserves first
        // occurrence, so the order is still the collection's.
        List<Type> replaced =
        [
            .. additions
                .Select(addition => addition.ServiceType)
                .Where(removedServiceTypes.Contains)
                .Distinct(),
        ];

        return new PluginContributionRecord(plugin, additions, removals, replaced);
    }

    private static Dictionary<ServiceDescriptor, int> OccurrencesOf(
        IEnumerable<ServiceDescriptor> descriptors
    )
    {
        // Reference identity, for the reason RecordingServiceCollection's own snapshot gives:
        // ServiceDescriptor does not override equality today and this must not depend on that.
        Dictionary<ServiceDescriptor, int> occurrences = new(ReferenceEqualityComparer.Instance);

        foreach (ServiceDescriptor descriptor in descriptors)
        {
            occurrences[descriptor] = occurrences.TryGetValue(descriptor, out int count) ? count + 1 : 1;
        }

        return occurrences;
    }

    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
            : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
}
