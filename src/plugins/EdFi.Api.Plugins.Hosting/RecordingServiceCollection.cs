// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// The service collection a plugin's contribution hook is handed: a pass-through that refuses a
/// removal the plugin is not allowed to make, and a registration of a logging service the host
/// reserves, before the call reaches the real collection.
/// </summary>
/// <remarks>
/// <para>
/// Refusing early is the only reason this type exists. What each plugin contributed is worked out by
/// snapshotting the real collection before its hook and diffing after, which needs no wrapper; but a
/// diff taken after the hook returned would see a removal already applied, with the host's descriptor
/// already gone. So the removal rules sit in front of the collection and everything else delegates.
/// </para>
/// <para>
/// The logging reservation is here for a different reason, and it is not that it must fire early. It
/// is here because the post-container audit needs a logger of its own before it can report anything,
/// so a rule that only ran there would be enforced by machinery the offending registration has already
/// taken over. Refusing the incoming descriptor keeps the host's logging intact for the report, the
/// same property the removal rules get from where they sit.
/// </para>
/// <para>
/// It therefore hides, reorders and projects nothing, and it keeps no ledger of calls. The one thing
/// it remembers is the first refusal it raised, because a hook that catches that exception must not be
/// able to compose anyway; see <see cref="FirstRefusal"/>. An earlier
/// design masked replace-cardinality descriptors from the plugin's view; that is withdrawn as unsound,
/// because <c>RemoveAll&lt;T&gt;</c> and <c>Replace</c> walk the collection by index and write those
/// indices back to the real one, so a projection makes them remove descriptors nobody asked for. The
/// name is the design's, and it now describes the pre-hook snapshot this holds rather than any
/// recording of calls.
/// </para>
/// <para>
/// Each incoming write member rejects a null descriptor itself rather than passing it on.
/// <c>ServiceCollection</c> accepts null through all three, and the failure then surfaces from the
/// per-hook diff, which runs outside the invoker's handling of a hook's exceptions and so reports
/// nothing about the plugin that caused it. Rejecting it here puts the failure inside the hook, where
/// the invoker names the plugin.
/// </para>
/// </remarks>
internal sealed class RecordingServiceCollection : IServiceCollection
{
    private readonly IServiceCollection _inner;
    private readonly string _pluginName;
    private readonly TextWriter _diagnostics;

    /// <summary>
    /// The descriptors that were on the collection when the hook began, by reference.
    /// </summary>
    /// <remarks>
    /// Reference identity rather than equality. <see cref="ServiceDescriptor"/> does not override
    /// equality today, so the two coincide, and depending on that silently would make this rule turn
    /// on a decision taken in another package. A descriptor the plugin adds during its own hook is
    /// absent from this set, which is what makes a plugin replacing its own registration ordinary
    /// work.
    /// </remarks>
    private readonly HashSet<ServiceDescriptor> _preExisting;

    internal RecordingServiceCollection(IServiceCollection inner, string pluginName, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(pluginName);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _inner = inner;
        _pluginName = pluginName;
        _diagnostics = diagnostics;
        _preExisting = new HashSet<ServiceDescriptor>(inner, ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// The first refusal this wrapper raised, or null when it raised none.
    /// </summary>
    /// <remarks>
    /// Throwing alone does not carry the host's decision: registration code that wraps best-effort
    /// setup in a catch-all swallows the exception and returns as though nothing happened, and the
    /// invoker has to be able to fail the composition anyway. The first refusal is kept rather than the
    /// last, because it is the rule that fired before the rest of the hook ran on top of it.
    /// </remarks>
    internal PluginCompositionException? FirstRefusal { get; private set; }

    public int Count => _inner.Count;

    public bool IsReadOnly => _inner.IsReadOnly;

    public ServiceDescriptor this[int index]
    {
        get => _inner[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            // The incoming rule first, so the same descriptor is refused for the same reason whichever
            // write member carried it; the slot it happens to target is what the second rule is about.
            RefuseReservedLoggingRegistration(value);
            RefuseDisplacementOfPreExisting(DescriptorAt(index));
            _inner[index] = value;
        }
    }

    public void Add(ServiceDescriptor item)
    {
        ArgumentNullException.ThrowIfNull(item);
        RefuseReservedLoggingRegistration(item);
        _inner.Add(item);
    }

    public void Insert(int index, ServiceDescriptor item)
    {
        ArgumentNullException.ThrowIfNull(item);
        RefuseReservedLoggingRegistration(item);
        _inner.Insert(index, item);
    }

    public void Clear()
    {
        // Always fatal, whatever the collection happens to hold: clearing removes the host's
        // registrations by construction, and a rule that depended on what was in the collection at the
        // moment would pass for a plugin that ran first.
        throw Report(
            PluginCompositionFailure.ServiceCollectionCleared,
            $"plugin '{PluginDiagnosticText.Quote(_pluginName)}' called Clear() on the host's service "
                + "collection during ContributeServices, which would remove every registration the host "
                + "made. A plugin contributes registrations; it does not remove the host's."
        );
    }

    public bool Remove(ServiceDescriptor item)
    {
        if (item is not null)
        {
            RefuseDisplacementOfPreExisting(item);
        }

        return _inner.Remove(item!);
    }

    public void RemoveAt(int index)
    {
        RefuseDisplacementOfPreExisting(DescriptorAt(index));
        _inner.RemoveAt(index);
    }

    public bool Contains(ServiceDescriptor item) => _inner.Contains(item);

    public int IndexOf(ServiceDescriptor item) => _inner.IndexOf(item);

    public void CopyTo(ServiceDescriptor[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);

    public IEnumerator<ServiceDescriptor> GetEnumerator() => _inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_inner).GetEnumerator();

    /// <summary>
    /// The descriptor an index refers to, or null when the index is outside the collection.
    /// </summary>
    /// <remarks>
    /// An out-of-range index is the real collection's business to report. Reading it here to guard it
    /// would replace <see cref="ArgumentOutOfRangeException"/> with an exception from inside the
    /// wrapper, which tells a plugin author less about what their code did.
    /// </remarks>
    private ServiceDescriptor? DescriptorAt(int index) =>
        index >= 0 && index < _inner.Count ? _inner[index] : null;

    /// <summary>
    /// Refuses an incoming unkeyed registration of a logging service the host reserves.
    /// </summary>
    /// <remarks>
    /// A reservation, not duplicate detection: nothing here reads the collection, so the rule holds
    /// whether or not the host already registered that exact service type. What it protects is the
    /// resolve a host component makes later, which takes the last unkeyed registration, so a plugin
    /// can displace the host's logging by adding rather than by removing and no removal rule would
    /// ever see the call.
    /// <para>
    /// Keyed descriptors pass, because an unkeyed resolve reaches none of them, and so does an
    /// <c>ILoggerProvider</c>, which is enumerated beside the host's rather than resolved instead of
    /// it. Those two carve-outs are what keep a plugin's own sink ordinary work.
    /// </para>
    /// </remarks>
    private void RefuseReservedLoggingRegistration(ServiceDescriptor descriptor)
    {
        if (
            descriptor.IsKeyedService
            || !HostOwnedServiceTypes.IsReservedLoggingService(descriptor.ServiceType)
        )
        {
            return;
        }

        throw Report(
            PluginCompositionFailure.ReservedLoggingServiceRegistered,
            $"plugin '{PluginDiagnosticText.Quote(_pluginName)}' registered "
                + $"'{TypeNameOf(descriptor.ServiceType)}' with no service key. The host reserves its "
                + "own unkeyed logging services, whether or not it has already registered one, because "
                + "an unkeyed resolve takes the last registration and this one would be it. A "
                + "plugin that wants its own sink adds an "
                + $"'{TypeNameOf(typeof(ILoggerProvider))}' instead, which composes with the host's; a "
                + "keyed logging registration is also permitted, because nothing the host resolves "
                + "reaches one."
        );
    }

    private void RefuseDisplacementOfPreExisting(ServiceDescriptor? descriptor)
    {
        if (descriptor is null || !_preExisting.Contains(descriptor))
        {
            return;
        }

        // The logging set is tested first so that the more specific rule is the one reported. The two
        // sets do not overlap as they stand: the logging service types are declared in
        // Microsoft.Extensions.Logging.Abstractions and carry no host prefix.
        if (HostOwnedServiceTypes.IsLoggingPipeline(descriptor.ServiceType))
        {
            throw Report(
                PluginCompositionFailure.LoggingPipelineDescriptorDisplaced,
                $"plugin '{PluginDiagnosticText.Quote(_pluginName)}' removed or overwrote the "
                    + $"pre-existing descriptor for '{TypeNameOf(descriptor.ServiceType)}', which carries "
                    + "the host's logging. Clearing the host's providers silences every host log line and "
                    + "the record of what this plugin did with it. A plugin that wants its own sink adds "
                    + "a provider instead."
            );
        }

        if (HostOwnedServiceTypes.IsHostOwned(descriptor.ServiceType))
        {
            throw Report(
                PluginCompositionFailure.HostOwnedDescriptorDisplaced,
                $"plugin '{PluginDiagnosticText.Quote(_pluginName)}' removed or overwrote the "
                    + $"pre-existing host descriptor for '{TypeNameOf(descriptor.ServiceType)}'. A plugin "
                    + "contributes registrations; it does not edit the host's. Removing or replacing a "
                    + "descriptor this plugin registered itself is permitted."
            );
        }
    }

    /// <summary>
    /// Writes the refusal to the loader's diagnostic channel, records it, and returns the exception to
    /// throw.
    /// </summary>
    /// <remarks>
    /// Written before the call reaches the real collection, so the descriptors the refusal is about are
    /// still in place when it is reported. The channel is the loader's own rather than a logger,
    /// because the pipeline a logger would use is exactly what one of these rules protects. Recorded as
    /// well as returned so that swallowing the exception cannot turn the refusal into a log line; the
    /// caller still throws it immediately, which is what keeps the offending call from landing.
    /// </remarks>
    private PluginCompositionException Report(PluginCompositionFailure reason, string message)
    {
        _diagnostics.WriteLine($"plugin composition refused: {message}");

        PluginCompositionException refusal = new(reason, _pluginName, message);
        FirstRefusal ??= refusal;

        return refusal;
    }

    private static string TypeNameOf(Type serviceType) =>
        PluginDiagnosticText.Quote(serviceType.FullName ?? serviceType.Name);
}
