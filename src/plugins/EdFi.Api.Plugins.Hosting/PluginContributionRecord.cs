// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// A descriptor a plugin removed from the collection, with what it displaced.
/// </summary>
/// <param name="ServiceType">The service type the removed descriptor was registered under.</param>
/// <param name="DisplacedImplementationType">
/// The implementation the removal took out of service, or <see langword="null"/> when the descriptor
/// named none, which is what a factory registration looks like.
/// </param>
/// <param name="Descriptor">The descriptor itself, so a later check can read its shape.</param>
public sealed record PluginDescriptorDisplacement(
    Type ServiceType,
    Type? DisplacedImplementationType,
    ServiceDescriptor Descriptor
);

/// <summary>
/// What one plugin contributed, worked out by comparing the real collection before and after its hook.
/// </summary>
/// <remarks>
/// <para>
/// Hooks run one at a time in allowlist order, so each comparison belongs to exactly one plugin. The
/// comparison is by reference and preserves multiplicity: a plugin that adds the same descriptor
/// instance twice contributed two descriptors, and a count that collapsed them would under-report a
/// cardinality conflict downstream.
/// </para>
/// <para>
/// The loader's inventory and its host-first substitutions are read through this record rather than
/// copied into it, because both change after the hook returns: an assembly first touched inside a hook
/// loads then, and a substitution first happens then. This is also the single source the guard's checks
/// and the inventory event both read, so neither can drift from the other.
/// </para>
/// <para>
/// <strong>Every member here is host implementation data, not a promised third-party API.</strong> It
/// is public because the audit function and the frontend's startup task live in different assemblies
/// and both read it, and it carries what those two need rather than a curated surface for a plugin
/// author. A member with no current reader is kept when it is what the inventory event or a later
/// composition phase is specified to report, and none of it is a compatibility commitment to anyone
/// outside this repository.
/// </para>
/// </remarks>
public sealed class PluginContributionRecord
{
    internal PluginContributionRecord(
        LoadedPlugin plugin,
        IReadOnlyList<ServiceDescriptor> additions,
        IReadOnlyList<PluginDescriptorDisplacement> removals,
        IReadOnlyList<Type> replacedServiceTypes
    )
    {
        Plugin = plugin;
        Additions = new ReadOnlyCollection<ServiceDescriptor>([.. additions]);
        Removals = new ReadOnlyCollection<PluginDescriptorDisplacement>([.. removals]);
        ReplacedServiceTypes = new ReadOnlyCollection<Type>([.. replacedServiceTypes]);
    }

    /// <summary>The plugin this record belongs to, as the loader returned it.</summary>
    public LoadedPlugin Plugin { get; }

    /// <summary>The plugin's name, which the loader has already checked against its directory.</summary>
    public string PluginName => Plugin.Name;

    /// <summary>
    /// Every descriptor on the collection after the hook that was not on it before, in collection
    /// order, one entry per occurrence.
    /// </summary>
    /// <remarks>
    /// Historical, and not filtered to what survived the plugins that ran after this one. A check that
    /// needs survival intersects this against the composition snapshot by reference; narrowing the
    /// list itself would make the inventory event under-report what the plugin did.
    /// </remarks>
    public IReadOnlyList<ServiceDescriptor> Additions { get; }

    /// <summary>
    /// Every descriptor that was on the collection before the hook and is not on it after, in the order
    /// they stood, one entry per occurrence.
    /// </summary>
    /// <remarks>
    /// A removal reaching this list is one the wrapper permitted: a host-owned or logging-pipeline
    /// descriptor never gets here, because that call is refused before it lands. The list is
    /// historical and stays so. A removal that took another plugin's declared-contract registration
    /// out of service is still permitted and still recorded here; what it can fail is the surviving
    /// contribution check, which reads the composition snapshot rather than this list.
    /// </remarks>
    public IReadOnlyList<PluginDescriptorDisplacement> Removals { get; }

    /// <summary>
    /// The service types this plugin both added and removed a descriptor for, which is what a
    /// replacement looks like in a comparison that only sees the two ends.
    /// </summary>
    /// <remarks>
    /// Reported rather than checked. No rule keys on it: a plugin replacing its own descriptor is
    /// routine work, and a replacement over a descriptor it may not touch was already refused at the
    /// wrapper. It is here because the inventory event distinguishes a replacement from a bare
    /// removal, and deriving that again at the log site from two lists would be the second source
    /// this record exists to avoid.
    /// </remarks>
    public IReadOnlyList<Type> ReplacedServiceTypes { get; }

    /// <summary>The plugin's declared-file inventory, as it stands now.</summary>
    public IReadOnlyList<PluginInventoryRow> MaterializeInventory() => Plugin.MaterializeInventory();

    /// <summary>The assemblies the host has served this plugin in place of its own, as it stands now.</summary>
    public IReadOnlyList<HostFirstSubstitution> MaterializeSubstitutions() =>
        Plugin.MaterializeSubstitutions();
}
