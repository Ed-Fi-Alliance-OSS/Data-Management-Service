// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Every plugin the loader returned, in allowlist order.
/// </summary>
/// <remarks>
/// An aggregate rather than a bare list because the two composition phases live on it: the
/// configuration phase, which a host runs as soon as loading returns, and the service phase, which it
/// runs while its container is still open. Both need somewhere to live that a caller already holds.
/// </remarks>
public sealed class LoadedPlugins
{
    /// <summary>
    /// Creates an aggregate over the given plugins, in allowlist order. Internal because the loader is
    /// the only thing that has plugins to put in one.
    /// </summary>
    internal LoadedPlugins(IReadOnlyList<LoadedPlugin> plugins, IReadOnlyList<PluginLoadWarning> warnings)
    {
        Plugins = plugins;
        Warnings = warnings;
    }

    /// <summary>
    /// The value returned when no plugin was asked for, which is the shipped default.
    /// </summary>
    public static LoadedPlugins Empty { get; } = new([], []);

    /// <summary>
    /// The loaded plugins, in the order the operator wrote them, which is the invocation order for
    /// both composition phases.
    /// </summary>
    public IReadOnlyList<LoadedPlugin> Plugins { get; }

    /// <summary>
    /// What the loader warned about, carried forward so the host can replay it through a real logger.
    /// </summary>
    /// <remarks>
    /// The loader's own channel is <see cref="Console.Error"/>, because it runs before any logging
    /// pipeline exists. A deployment collecting application logs rather than container stdout sees
    /// nothing written there, so these travel as data to the point where a logger exists.
    /// </remarks>
    public IReadOnlyList<PluginLoadWarning> Warnings { get; }

    /// <summary>
    /// The plugins whose configuration hook added at least one source and passed the guard.
    /// </summary>
    /// <remarks>
    /// Historical: a plugin stays here whatever happens to its sources afterwards. The service phase
    /// copies the fact into each plugin's contribution record, which is how the audit learns that a
    /// plugin contributed configuration. Only the fact is kept, not the sources, because nothing reads
    /// more than that.
    /// </remarks>
    private readonly HashSet<LoadedPlugin> _configurationContributors = new(
        ReferenceEqualityComparer.Instance
    );

    /// <summary>
    /// Invokes every plugin's configuration contribution hook, in allowlist order, and places the
    /// sources each one added below the operator's explicit sources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each hook is handed a staging builder as its builder and <paramref name="configuration"/> as its
    /// bootstrap configuration. The staging builder starts with the host's sources, by reference, and
    /// the host's builder properties, so a relative file path resolves as it would against the host.
    /// Adding to it loads nothing. The live manager is what lets a plugin read a value an earlier
    /// plugin supplied.
    /// </para>
    /// <para>
    /// The staging builder is what keeps each source loading once. A <see cref="ConfigurationManager"/>
    /// rebuilds and reloads every source on every change to its source list, so adding a plugin's
    /// sources to it one at a time, and then moving them, would reload the JSON files, every earlier
    /// plugin's sources and the plugin's own once per change. A vault-backed source would be called
    /// repeatedly on every startup, and a failure on a reload would escape without naming the plugin.
    /// </para>
    /// <para>
    /// Contribution is additive only. The staging builder's sources are compared by reference after
    /// the hook against the host's, and a hook that removed a source present before it began, or
    /// changed the relative order of those sources, fails the composition naming the plugin. What the
    /// comparison cannot see is a change to a pre-existing source object's own properties, or a change
    /// made to the host's own list by casting the bootstrap configuration back to a builder, both of
    /// which are trust assumptions rather than controls.
    /// </para>
    /// <para>
    /// After a hook passes, the sources it added are built together, once, in the order the plugin
    /// added them, and a failure there fails the composition naming the plugin. The result goes into
    /// the host as one <see cref="ChainedConfigurationSource"/>, immediately below the last
    /// <see cref="EnvironmentVariablesConfigurationSource"/> present when this phase began, which is
    /// the one change this makes to the host's list for that plugin. That keeps every plugin source
    /// above the JSON sources and below the operator's environment and command-line sources, which
    /// were never moved, and puts each later plugin's sources above an earlier one's. The chained
    /// source's own reload does not reload what it wraps, so a later plugin's insert does not call an
    /// earlier plugin's sources again. A host with no environment source at all gets the plugin's
    /// source on top of its list, above a command-line source if it has one; neither host that runs
    /// this phase is shaped that way. The loader adds no source of its own.
    /// </para>
    /// </remarks>
    /// <exception cref="PluginCompositionException">
    /// A hook removed or reordered a pre-existing source, or threw, or a source it added failed to
    /// load.
    /// </exception>
    public void ContributeConfiguration(ConfigurationManager configuration) =>
        ContributeConfiguration(configuration, Console.Error);

    /// <summary>
    /// The overload the public one calls with <see cref="Console.Error"/>, so a test can read the
    /// channel without redirecting the process's own.
    /// </summary>
    internal void ContributeConfiguration(ConfigurationManager configuration, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(diagnostics);

        IConfigurationBuilder host = configuration;

        // Found once, before any hook runs, and held by reference. A plugin may add environment sources
        // of its own, and those are plugin sources to be placed rather than the operator's surface to
        // place them under.
        IConfigurationSource? operatorEnvironment = host.Sources.LastOrDefault(source =>
            source is EnvironmentVariablesConfigurationSource
        );

        foreach (LoadedPlugin plugin in Plugins)
        {
            // Before the call, for the reason ContributeServices gives: a hook that never returns has to
            // leave the plugin it entered as the last line on the channel.
            diagnostics.WriteLine(
                $"invoking ContributeConfiguration on {PluginDiagnosticText.Quote(plugin.Name)}"
            );

            ConfigurationBuilder staging = StagingBuilderFor(host);
            List<IConfigurationSource> before = [.. host.Sources];

            try
            {
                plugin.Instance.ContributeConfiguration(staging, configuration);
            }
            catch (Exception exception)
            {
                string message =
                    $"plugin '{PluginDiagnosticText.Quote(plugin.Name)}' threw from ContributeConfiguration, "
                    + "the configuration contribution phase: "
                    + $"{PluginDiagnosticText.Quote(exception.GetType().FullName)}: "
                    + PluginDiagnosticText.Quote(exception.Message);

                throw Refuse(
                    PluginCompositionFailure.ContributeConfigurationThrew,
                    plugin,
                    message,
                    diagnostics,
                    exception
                );
            }

            List<int> additions = AdditionsOf(plugin, before, staging.Sources, diagnostics);

            if (additions.Count == 0)
            {
                continue;
            }

            IConfigurationRoot contributed = Build(plugin, host, staging.Sources, additions, diagnostics);

            _configurationContributors.Add(plugin);

            Place(host.Sources, contributed, operatorEnvironment);
        }
    }

    /// <summary>
    /// A builder holding the host's sources and properties, for one hook to add to.
    /// </summary>
    /// <remarks>
    /// The properties are copied rather than shared, so a hook that sets its own base path changes
    /// where its own relative paths resolve and not where the host's do.
    /// </remarks>
    private static ConfigurationBuilder StagingBuilderFor(IConfigurationBuilder host)
    {
        ConfigurationBuilder staging = new();

        CopyProperties(host, staging);

        foreach (IConfigurationSource source in host.Sources)
        {
            staging.Sources.Add(source);
        }

        return staging;
    }

    private static void CopyProperties(IConfigurationBuilder from, ConfigurationBuilder to)
    {
        foreach ((string key, object value) in from.Properties)
        {
            to.Properties[key] = value;
        }
    }

    /// <summary>
    /// Builds one hook's additions together, in list order, which is the only time they load here.
    /// </summary>
    private static IConfigurationRoot Build(
        LoadedPlugin plugin,
        IConfigurationBuilder host,
        IList<IConfigurationSource> staged,
        List<int> additions,
        TextWriter diagnostics
    )
    {
        ConfigurationBuilder contributed = new();

        CopyProperties(host, contributed);

        foreach (int index in additions)
        {
            contributed.Sources.Add(staged[index]);
        }

        try
        {
            return contributed.Build();
        }
        catch (Exception exception)
        {
            string message =
                $"plugin '{PluginDiagnosticText.Quote(plugin.Name)}' added a configuration source from "
                + "ContributeConfiguration that failed to load: "
                + $"{PluginDiagnosticText.Quote(exception.GetType().FullName)}: "
                + PluginDiagnosticText.Quote(exception.Message);

            throw Refuse(
                PluginCompositionFailure.ConfigurationSourceLoadFailed,
                plugin,
                message,
                diagnostics,
                exception
            );
        }
    }

    /// <summary>
    /// Compares the sources either side of one configuration hook and returns the positions of the
    /// sources the hook added, in list order.
    /// </summary>
    /// <remarks>
    /// The pre-existing sources are matched in order, by reference, as a subsequence of what is there
    /// afterwards. Anything left unmatched is the plugin's own addition, wherever it put it: an insert
    /// adds a source just as an append does, and placement moves it either way. If the subsequence does
    /// not complete, a pre-existing source is either gone or out of order, and counting occurrences
    /// tells the two apart.
    /// </remarks>
    private static List<int> AdditionsOf(
        LoadedPlugin plugin,
        List<IConfigurationSource> before,
        IList<IConfigurationSource> after,
        TextWriter diagnostics
    )
    {
        List<int> additions = [];
        int matched = 0;

        for (int index = 0; index < after.Count; index++)
        {
            if (matched < before.Count && ReferenceEquals(after[index], before[matched]))
            {
                matched++;
            }
            else
            {
                additions.Add(index);
            }
        }

        if (matched == before.Count)
        {
            return additions;
        }

        Dictionary<IConfigurationSource, int> remaining = new(ReferenceEqualityComparer.Instance);

        foreach (IConfigurationSource source in after)
        {
            remaining[source] = remaining.TryGetValue(source, out int count) ? count + 1 : 1;
        }

        for (int position = 0; position < before.Count; position++)
        {
            IConfigurationSource source = before[position];

            if (remaining.TryGetValue(source, out int count) && count > 0)
            {
                remaining[source] = count - 1;
                continue;
            }

            throw Refuse(
                PluginCompositionFailure.ConfigurationSourceRemoved,
                plugin,
                $"plugin '{PluginDiagnosticText.Quote(plugin.Name)}' removed configuration source "
                    + $"{position} ({DescribeSource(source)}) from ContributeConfiguration. A plugin may "
                    + "add configuration sources; it may not remove one that was present before its "
                    + "hook ran.",
                diagnostics
            );
        }

        IConfigurationSource displaced = before[matched];

        throw Refuse(
            PluginCompositionFailure.ConfigurationSourceReordered,
            plugin,
            $"plugin '{PluginDiagnosticText.Quote(plugin.Name)}' moved configuration source {matched} "
                + $"({DescribeSource(displaced)}) from ContributeConfiguration. A plugin may add "
                + "configuration sources; it may not change the order of the ones present before its "
                + "hook ran, because that order is the host's precedence.",
            diagnostics
        );
    }

    /// <summary>
    /// Inserts one hook's built additions immediately below the operator's environment source, as one
    /// source.
    /// </summary>
    /// <remarks>
    /// The chained source does not dispose what it wraps. The manager disposes and rebuilds every
    /// provider on each change to its list, and a later plugin's insert is one, so a chained source
    /// that disposed its configuration would be rebuilt over a disposed one. What it wraps lives as
    /// long as the process, as the host's own configuration does.
    /// </remarks>
    private static void Place(
        IList<IConfigurationSource> sources,
        IConfigurationRoot contributed,
        IConfigurationSource? operatorEnvironment
    )
    {
        ChainedConfigurationSource source = new()
        {
            Configuration = contributed,
            ShouldDisposeConfiguration = false,
        };

        if (operatorEnvironment is null)
        {
            sources.Add(source);
            return;
        }

        sources.Insert(IndexOfReference(sources, operatorEnvironment), source);
    }

    private static int IndexOfReference(IList<IConfigurationSource> sources, IConfigurationSource source)
    {
        for (int index = 0; index < sources.Count; index++)
        {
            if (ReferenceEquals(sources[index], source))
            {
                return index;
            }
        }

        // Unreachable unless a hook reached the host's own list by casting its bootstrap configuration
        // back to a builder, which is outside the contract: hooks add to a staging builder, and nothing
        // else in this phase removes a source.
        throw new InvalidOperationException("The operator's environment configuration source is gone.");
    }

    /// <summary>
    /// The source's type name and nothing else. A source's own properties can carry a path or a
    /// prefix, and its data is configuration values, none of which belongs on a diagnostic channel.
    /// </summary>
    private static string DescribeSource(IConfigurationSource source) =>
        PluginDiagnosticText.Quote(source.GetType().FullName);

    private static PluginCompositionException Refuse(
        PluginCompositionFailure reason,
        LoadedPlugin plugin,
        string message,
        TextWriter diagnostics,
        Exception? innerException = null
    )
    {
        diagnostics.WriteLine($"plugin configuration refused: {message}");

        return new PluginCompositionException(reason, plugin.Name, message, innerException);
    }

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
    /// A hook that catches its own refusal and returns still fails the composition. The refusal is the
    /// host's decision and not the plugin's to handle: the wrapper refused before the write landed, so
    /// the host's descriptors are intact either way, but the hook stopped partway through its own
    /// registrations and admitting it would leave a half-composed plugin in a built container.
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
    /// A hook removed something it may not remove, cleared the collection, threw, or caught one of
    /// those refusals and returned.
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

            records.Add(RecordOf(plugin, before, services, _configurationContributors.Contains(plugin)));
        }

        return new PluginAuditInput(registry, records, [.. services], Warnings);
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
        catch (PluginCompositionException refusal)
        {
            // Already the named fatal, already reported by the wrapper that raised it. Wrapping it
            // again would bury the rule that fired under a generic "the hook threw". A hook that
            // swallowed an earlier refusal and then tripped a second one is reported on the first,
            // which is the rule that fired before the rest of the hook ran.
            if (wrapper.FirstRefusal is { } earlier && !ReferenceEquals(earlier, refusal))
            {
                ExceptionDispatchInfo.Capture(earlier).Throw();
            }

            throw;
        }
        catch (Exception exception)
        {
            // A refusal the hook caught outranks whatever it failed on next: the later failure is
            // ordinarily a consequence of the work the refusal cut short, so reporting the symptom
            // would hide the host-owned rule that actually fired.
            ThrowFirstRefusal(wrapper);

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

        // The hook returned, which is not the same as having composed. Nothing else can notice a
        // refusal it caught: the wrapper refused before the write landed, so the diff sees no removal,
        // and the audit sees a plugin that registered a contract like any other.
        ThrowFirstRefusal(wrapper);
    }

    /// <summary>
    /// Throws the first refusal the wrapper raised, when a hook caught it and carried on.
    /// </summary>
    /// <remarks>
    /// Thrown through <see cref="ExceptionDispatchInfo"/> rather than with a plain <c>throw</c> of the
    /// exception object, which would overwrite the stack trace the original throw recorded with this
    /// frame and lose the call inside the hook that the refusal is about.
    /// </remarks>
    private static void ThrowFirstRefusal(RecordingServiceCollection wrapper)
    {
        if (wrapper.FirstRefusal is { } refusal)
        {
            ExceptionDispatchInfo.Capture(refusal).Throw();
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
        IServiceCollection after,
        bool contributedConfiguration
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
                        PluginDescriptorFacts.ImplementationTypeOf(descriptor),
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

        return new PluginContributionRecord(plugin, additions, removals, replaced, contributedConfiguration);
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
}
