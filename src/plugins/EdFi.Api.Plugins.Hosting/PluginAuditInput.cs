// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Everything the plugin startup checks need, carried from the composition phase to the moment after
/// the container is built.
/// </summary>
/// <remarks>
/// <para>
/// The records are produced while the service collection is still open and the checks that read them
/// run after it is closed, so something has to hold them in between. A host registers the instance the
/// invoker returned and the task that reads it takes this type by constructor; nothing resolves the
/// service collection, and registering an instance means the container activates nothing.
/// </para>
/// <para>
/// The constructor is public because a plugin can register one of these and the host's has to win
/// anyway. It wins by ordering rather than by inaccessibility: the host registers its own after every
/// hook has run, and a single-service resolve takes the last registration. An inaccessible constructor
/// would make that property untestable and would rest the guarantee on the wrong thing.
/// </para>
/// </remarks>
public sealed class PluginAuditInput
{
    /// <summary>
    /// Creates an input over the contracts a host declares, what each plugin contributed, and the
    /// descriptors on the collection when composition finished.
    /// </summary>
    public PluginAuditInput(
        PluginContractRegistry registry,
        IReadOnlyList<PluginContributionRecord> records,
        IReadOnlyList<ServiceDescriptor> descriptorsAfterContribution
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(descriptorsAfterContribution);

        Registry = registry;
        Records = new ReadOnlyCollection<PluginContributionRecord>([.. records]);
        DescriptorsAfterContribution = new ReadOnlyCollection<ServiceDescriptor>([
            .. descriptorsAfterContribution,
        ]);
    }

    /// <summary>The contracts the host declares, with the cardinality it holds for each.</summary>
    public PluginContractRegistry Registry { get; }

    /// <summary>One record per plugin whose hook ran, in allowlist order.</summary>
    public IReadOnlyList<PluginContributionRecord> Records { get; }

    /// <summary>
    /// The descriptors on the service collection at the moment the last hook returned, in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what lets a later check tell a descriptor no plugin contributed from one a plugin did,
    /// and what lets it tell a registration that survived composition from one a later hook removed,
    /// without resolving the collection out of the container, which a plugin is permitted to register
    /// its own of.
    /// </para>
    /// <para>
    /// What it is not is the container's final descriptor set: a host goes on registering after
    /// composition, and anything registered then is absent here. A check that reads this must say what
    /// it is reading rather than describing it as everything the container holds. This moment is also
    /// the deadline the audit's composition contract sets for declared-contract registration:
    /// unrelated infrastructure the host registers afterwards is supported and unexamined, while a
    /// declared-contract registration or removal made after it is outside what the audit undertakes to
    /// cover.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ServiceDescriptor> DescriptorsAfterContribution { get; }
}
