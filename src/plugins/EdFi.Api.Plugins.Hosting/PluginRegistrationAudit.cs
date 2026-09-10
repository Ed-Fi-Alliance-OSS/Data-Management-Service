// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// The plugin checks that can only be made once the container exists.
/// </summary>
/// <remarks>
/// <para>
/// Host-agnostic and free of any startup-task machinery: it takes what the composition phase recorded
/// and a built provider, and returns findings. Each host wraps it in whatever its own startup
/// mechanism is and takes its own fatal path.
/// </para>
/// <para>
/// Four things are checked before anything is activated and one after. The four are decided from the
/// records, the composition snapshot and the host's contract registry alone, so a collection that is
/// already wrong is never activated: activating registrations that a static check has already refused
/// would run third-party constructors on a candidate the host has decided not to accept.
/// </para>
/// <para>
/// <strong>The composition contract this audit supports.</strong> Declared-contract registration must
/// be finished by the moment composition ends, which is the moment
/// <see cref="PluginAuditInput.DescriptorsAfterContribution"/> is taken. A host goes on registering
/// after that, and unrelated infrastructure registered later is supported and unaffected. A
/// declared-contract registration, removal or key added after that snapshot is outside what this
/// audit undertakes to cover: the static checks reason about what the snapshot and the records hold,
/// and the probe resolves the groups discovered from them. That is a stated boundary rather than a
/// claim of detection, and nothing here is a promise to notice every possible later registration.
/// </para>
/// </remarks>
public static class PluginRegistrationAudit
{
    /// <summary>
    /// Runs the checks. Returns findings rather than throwing, so a caller can report all of them.
    /// </summary>
    /// <param name="input">What the composition phase recorded, and the contracts the host declares.</param>
    /// <param name="rootServiceProvider">
    /// The container the host built. Used for one throwaway scope and nothing else: no second container
    /// is built and no descriptor is rewritten.
    /// </param>
    /// <param name="cancellationToken">Cancels before the activation pass begins.</param>
    public static async Task<PluginAuditResult> AuditAsync(
        PluginAuditInput input,
        IServiceProvider rootServiceProvider,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rootServiceProvider);

        HashSet<Type> declaredContracts = [.. input.Registry.Entries.Select(entry => entry.Contract)];

        List<PluginAuditFinding> findings = [];

        AuditReplaceCardinality(input, findings);
        AuditHostOwnedDisplacement(input, declaredContracts, findings);
        AuditWildcardKeyedContracts(input, declaredContracts, findings);
        AuditContractsRegistered(input, declaredContracts, findings);

        if (findings.Count > 0)
        {
            // A static refusal stops here. The registrations below belong to a candidate the host has
            // already declined, and activating them would construct third-party types for a boot that
            // is not going to happen.
            return new PluginAuditResult(findings, scopeCleanupFailure: null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await ActivateAsync(input, rootServiceProvider).ConfigureAwait(false);
    }

    /// <summary>
    /// A replace-cardinality contract has one claimant. Two claims are fatal, whether they come from
    /// two plugins or from one plugin registering twice.
    /// </summary>
    /// <remarks>
    /// Counted over the descriptors the per-hook comparison attributed to plugins, by occurrence, so a
    /// plugin that registered the same descriptor instance twice has claimed twice. A pre-existing host
    /// default is in nobody's record and is deliberately not counted: a replace contract exists to
    /// displace one, so a host default plus one claim is two descriptors and exactly one claim, and
    /// counting descriptors on the collection instead would refuse the case that must pass.
    /// Keyed-ness is not a way to make a second claim invisible, so every attributed descriptor for the
    /// contract counts.
    /// <para>
    /// The claims counted are historical: every descriptor the diffs attributed, whether or not it
    /// survived composition. That is deliberate and is not the same accounting the no-contract check
    /// below uses. Two plugins that each claimed one replace contract have made an operator decision
    /// ambiguous, and a later removal of one claim does not resolve which of them the operator meant;
    /// counting survivors instead would let the pair pass whenever the second plugin tidied up after
    /// the first. Do not quietly align this with the survival rule below.
    /// </para>
    /// </remarks>
    private static void AuditReplaceCardinality(PluginAuditInput input, List<PluginAuditFinding> findings)
    {
        foreach (PluginContractEntry entry in input.Registry.Entries)
        {
            if (entry.Cardinality != Cardinality.Replace)
            {
                continue;
            }

            List<string> claimants = [];
            int claims = 0;

            foreach (PluginContributionRecord record in input.Records)
            {
                int claimsByThisPlugin = record.Additions.Count(descriptor =>
                    descriptor.ServiceType == entry.Contract
                );

                if (claimsByThisPlugin > 0)
                {
                    claims += claimsByThisPlugin;
                    claimants.Add(record.PluginName);
                }
            }

            if (claims <= 1)
            {
                continue;
            }

            findings.Add(
                new PluginAuditFinding(
                    PluginAuditFailure.ReplaceContractClaimedMoreThanOnce,
                    claimants,
                    entry.Contract,
                    $"the plugin contract '{TypeNameOf(entry.Contract)}' accepts one implementation and "
                        + $"{claims} were registered by {DescribePlugins(claimants)}. Only an operator can "
                        + "decide which one should be live, so remove all but one from Plugins:Allowed."
                )
            );
        }
    }

    /// <summary>
    /// A host service type is not a plugin's to claim, unless it is a declared contract.
    /// </summary>
    /// <remarks>
    /// The declared-contract exemption is evaluated first, which is the only reason a contract declared
    /// in a host assembly can be registered at all.
    /// </remarks>
    private static void AuditHostOwnedDisplacement(
        PluginAuditInput input,
        HashSet<Type> declaredContracts,
        List<PluginAuditFinding> findings
    )
    {
        foreach (PluginContributionRecord record in input.Records)
        {
            HashSet<Type> reported = [];

            foreach (Type serviceType in record.Additions.Select(descriptor => descriptor.ServiceType))
            {
                if (
                    declaredContracts.Contains(serviceType)
                    || !HostOwnedServiceTypes.IsHostOwned(serviceType)
                    || !reported.Add(serviceType)
                )
                {
                    continue;
                }

                findings.Add(
                    new PluginAuditFinding(
                        PluginAuditFailure.HostOwnedServiceTypeClaimed,
                        [record.PluginName],
                        contract: null,
                        $"plugin '{PluginDiagnosticText.Quote(record.PluginName)}' registered "
                            + $"'{TypeNameOf(serviceType)}', which the host owns and declares no "
                            + "plugin contract for. A plugin registers implementations of the contracts "
                            + "the host declares, and its own types; a plugin whose own assembly is named "
                            + "with an Ed-Fi host prefix is treated as the host's and lands here too."
                    )
                );
            }
        }
    }

    /// <summary>
    /// A declared contract registered under the wildcard service key cannot be activated at startup,
    /// whoever registered it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on net10.0: no enumerable resolve reaches a wildcard registration. Asking for the
    /// wildcard key returns the concrete-keyed registrations and never the wildcard itself, and asking
    /// for a concrete key enumerably returns nothing for it either. Only a single-service resolve with
    /// some concrete key reaches one, and the host holds no key to supply. So the startup activation
    /// this design requires of every declared-contract registration cannot be performed for this one
    /// shape, and it is refused rather than passed over in silence.
    /// </para>
    /// <para>
    /// There is no exemption for a registration the records do not attribute. The requirement is a
    /// property of the registration and not of who made it: a host wildcard registration of a declared
    /// contract is exactly as unactivatable as a plugin's, and exempting it would leave the audit
    /// asserting an activation it never performed. What changes with attribution is only what the
    /// finding can honestly say, so an unattributed one names no plugin rather than inventing one.
    /// </para>
    /// <para>
    /// This says nothing about keyed registrations in general: a declared contract under a concrete
    /// key is activated and supported, and a wildcard registration of anything that is not a declared
    /// contract is ordinary permitted work that nothing here inspects.
    /// </para>
    /// </remarks>
    private static void AuditWildcardKeyedContracts(
        PluginAuditInput input,
        HashSet<Type> declaredContracts,
        List<PluginAuditFinding> findings
    )
    {
        // Records first, so a descriptor a plugin contributed is reported against that plugin, and the
        // snapshot second for whatever is left. A descriptor in both is seen once, by reference, which
        // is what keeps a surviving plugin registration from being reported twice.
        HashSet<ServiceDescriptor> seen = new(ReferenceEqualityComparer.Instance);
        List<(ServiceDescriptor Descriptor, string? PluginName)> candidates = [];

        foreach (PluginContributionRecord record in input.Records)
        {
            // Where(seen.Add) is the filter and the de-duplication at once: the set answers false for
            // a descriptor already taken, so each reference reaches the list under one registrant.
            foreach (ServiceDescriptor descriptor in record.Additions.Where(seen.Add))
            {
                candidates.Add((descriptor, record.PluginName));
            }
        }

        foreach (ServiceDescriptor descriptor in input.DescriptorsAfterContribution.Where(seen.Add))
        {
            candidates.Add((descriptor, null));
        }

        HashSet<(Type Contract, string? PluginName)> reported = [];

        foreach ((ServiceDescriptor descriptor, string? pluginName) in candidates)
        {
            if (
                !descriptor.IsKeyedService
                || !ReferenceEquals(descriptor.ServiceKey, KeyedService.AnyKey)
                || !declaredContracts.Contains(descriptor.ServiceType)
                || !reported.Add((descriptor.ServiceType, pluginName))
            )
            {
                continue;
            }

            string registrant = pluginName is null
                ? "a registration this audit cannot attribute to a plugin"
                : $"plugin '{PluginDiagnosticText.Quote(pluginName)}'";

            findings.Add(
                new PluginAuditFinding(
                    PluginAuditFailure.DeclaredContractRegisteredUnderWildcardKey,
                    pluginName is null ? [] : [pluginName],
                    descriptor.ServiceType,
                    $"{registrant} registered the plugin contract "
                        + $"'{TypeNameOf(descriptor.ServiceType)}' under the wildcard service key. "
                        + "Every declared-contract registration is resolved once at startup, and a "
                        + "wildcard registration is reached only by resolving some concrete key, which "
                        + "the host does not hold for it. Register the contract without a key, or "
                        + "under a concrete key the host is given."
                )
            );
        }
    }

    /// <summary>
    /// A plugin whose hook ran and left no surviving declared-contract registration contributed
    /// nothing the host will call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured against the composition snapshot by reference identity, not against the plugin's
    /// additions alone. What the operator was promised is a live implementation, and a registration
    /// another plugin later removed is not one: the host resolves the contract and gets nothing the
    /// plugin contributed, which is the same outcome as a plugin that never registered it. Removing a
    /// permitted non-host descriptor stays permitted, so the removal is nobody's fatal; the
    /// composition it produces is what fails here, and it fails against the plugin left with nothing
    /// live.
    /// </para>
    /// <para>
    /// The two cases are told apart in the message rather than by a second reason code, because they
    /// are one rule with one remedy shape and two very different first questions: whether the plugin
    /// registered the wrong thing, or whether something else took its registration away.
    /// </para>
    /// <para>
    /// The rule the design states is a conjunction over both composition phases: no declared contract
    /// <em>and</em> no configuration source. Phase A does not exist yet, so only the first term is
    /// live here; the second arrives with the story that adds that phase, and this check has to gain
    /// it then rather than be read as already complete.
    /// </para>
    /// </remarks>
    private static void AuditContractsRegistered(
        PluginAuditInput input,
        HashSet<Type> declaredContracts,
        List<PluginAuditFinding> findings
    )
    {
        HashSet<ServiceDescriptor> survivors = new(
            input.DescriptorsAfterContribution,
            ReferenceEqualityComparer.Instance
        );

        foreach (PluginContributionRecord record in input.Records)
        {
            List<ServiceDescriptor> declaredAdditions =
            [
                .. record.Additions.Where(descriptor => declaredContracts.Contains(descriptor.ServiceType)),
            ];

            if (declaredAdditions.Exists(survivors.Contains))
            {
                continue;
            }

            // The record keeps every addition, surviving or not, so both messages can say what the
            // plugin actually did.
            IEnumerable<string> registered = record
                .Additions.Select(descriptor => TypeNameOf(descriptor.ServiceType))
                .Distinct(StringComparer.Ordinal);

            string registeredList = string.Join(", ", registered);
            string whatItRegistered = registeredList.Length == 0 ? "nothing at all" : registeredList;

            string message =
                declaredAdditions.Count == 0
                    ? $"plugin '{PluginDiagnosticText.Quote(record.PluginName)}' registered no plugin "
                        + "contract this host declares, so nothing it contributed will ever be called. "
                        + "It registered: "
                        + whatItRegistered
                        + ". The likeliest cause is a plugin allowlisted on the wrong host; a claim on "
                        + "a replace-cardinality contract made with TryAdd also lands here, because "
                        + "such a call declines silently and adds nothing."
                    : $"plugin '{PluginDiagnosticText.Quote(record.PluginName)}' registered "
                        + $"{declaredAdditions.Count} plugin contract registration(s) this host "
                        + "declares, and none of them survived service composition, so nothing it "
                        + "contributed will ever be called. It registered: "
                        + whatItRegistered
                        + ". Removing a descriptor that is neither host-owned nor part of the logging "
                        + "pipeline is permitted, so the likeliest cause is a later plugin in "
                        + "Plugins:Allowed removing this one's registration; reorder or remove one of "
                        + "them.";

            findings.Add(
                new PluginAuditFinding(
                    PluginAuditFailure.NoDeclaredContractRegistered,
                    [record.PluginName],
                    contract: null,
                    message
                )
            );
        }
    }

    /// <summary>
    /// Resolves every declared-contract registration once, from one asynchronous throwaway scope over
    /// the container the host built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reached only when every static check passed, so what it resolves is the surviving supported
    /// declared-contract registrations: the ones still on the collection when composition ended,
    /// under a key shape this audit supports. The unit is a group: the unkeyed registrations for a
    /// contract, resolved with one <c>GetServices</c>, and the registrations under each distinct
    /// concrete key discovered for the contract, resolved with one <c>GetKeyedServices</c> each. A
    /// group holding both a host descriptor and a plugin's under the same key is one group and one
    /// resolve. Measured on net10.0, those groups are disjoint and each resolve activates every
    /// descriptor in its own group exactly once, so nothing is activated twice. The wildcard key is
    /// never resolved, for the reason <see cref="AuditWildcardKeyedContracts"/> records, and a
    /// wildcard declared-contract registration has already been refused by the time this runs.
    /// </para>
    /// <para>
    /// What it resolves is the container the host built, read through the groups the snapshot and the
    /// records describe. It is not a re-derivation of the container's final contents, and it makes no
    /// claim about a declared-contract registration the host added after composition ended; the
    /// audit's supported composition contract, on the type-level remarks above, is where that
    /// boundary is stated.
    /// </para>
    /// <para>
    /// This is an activation check and not a disposal boundary, and the difference is measured rather
    /// than assumed: a singleton resolved inside the scope is cached in the root container and remains
    /// the instance production uses, while scoped and transient instances go with the scope. The scope
    /// is asynchronous because a service implementing only <see cref="IAsyncDisposable"/> makes a
    /// synchronous release throw.
    /// </para>
    /// </remarks>
    private static async Task<PluginAuditResult> ActivateAsync(
        PluginAuditInput input,
        IServiceProvider rootServiceProvider
    )
    {
        List<PluginAuditFinding> findings = [];
        AsyncServiceScope scope = rootServiceProvider.CreateAsyncScope();
        Exception? cleanupFailure = null;

        try
        {
            foreach (Type contract in input.Registry.Entries.Select(entry => entry.Contract))
            {
                Resolve(input, scope, contract, serviceKey: null, findings);

                foreach (object serviceKey in ConcreteKeysFor(input, contract))
                {
                    Resolve(input, scope, contract, serviceKey, findings);
                }
            }
        }
        finally
        {
            try
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Releasing the scope runs implementer code, and MS DI abandons the rest of the scope's
                // disposables after the first one throws. Letting that propagate would replace whatever
                // the checks concluded, a clean result included, with a cleanup failure.
                cleanupFailure = exception;
            }
        }

        return new PluginAuditResult(findings, cleanupFailure);
    }

    /// <summary>
    /// The distinct concrete service keys discovered for a contract, in the order they were
    /// registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discovered from the collection as it stood when composition finished <em>and</em> from what the
    /// per-hook comparisons attributed to plugins. The snapshot is the load-bearing source: it carries
    /// a keyed registration the <em>host</em> made, which belongs to no plugin's record, so a set
    /// derived from the records alone would form no group for its key and never resolve it, and an
    /// unconstructible host keyed registration would pass a check that refuses the equivalent unkeyed
    /// one. The records are read as well so that discovery does not depend on a descriptor still being
    /// present, which keeps this from silently changing shape when a permitted removal takes one away.
    /// A key discovered only from a record is a key whose descriptors are all gone: its group resolves
    /// to nothing and activates nothing, and a removed descriptor is not activatable by this or any
    /// other route.
    /// </para>
    /// <para>
    /// Ordinary key equality, which is what the container itself compares keys with. A key is an object
    /// a plugin or the host chose, so a key whose equality throws surfaces as a failure of this audit,
    /// which the caller reports the way it reports any other startup failure. An unkeyed descriptor
    /// contributes no key: the unkeyed group is resolved once on its own. The wildcard key is excluded
    /// for the reason <see cref="AuditWildcardKeyedContracts"/> records - nothing reaches such a
    /// registration by resolving a key, so forming a group for it would activate other keys twice and
    /// the wildcard not at all.
    /// </para>
    /// </remarks>
    private static List<object> ConcreteKeysFor(PluginAuditInput input, Type contract)
    {
        List<object> keys = [];
        HashSet<object> seen = [];

        foreach (ServiceDescriptor descriptor in input.DescriptorsAfterContribution)
        {
            AddKeyOf(descriptor, contract, keys, seen);
        }

        foreach (PluginContributionRecord record in input.Records)
        {
            foreach (ServiceDescriptor descriptor in record.Additions)
            {
                AddKeyOf(descriptor, contract, keys, seen);
            }
        }

        return keys;
    }

    private static void AddKeyOf(
        ServiceDescriptor descriptor,
        Type contract,
        List<object> keys,
        HashSet<object> seen
    )
    {
        if (
            descriptor.ServiceType == contract
            && descriptor.IsKeyedService
            && descriptor.ServiceKey is { } serviceKey
            && !ReferenceEquals(serviceKey, KeyedService.AnyKey)
            && seen.Add(serviceKey)
        )
        {
            keys.Add(serviceKey);
        }
    }

    private static void Resolve(
        PluginAuditInput input,
        AsyncServiceScope scope,
        Type contract,
        object? serviceKey,
        List<PluginAuditFinding> findings
    )
    {
        try
        {
            if (serviceKey is null)
            {
                _ = scope.ServiceProvider.GetServices(contract).ToList();
            }
            else
            {
                _ = scope.ServiceProvider.GetKeyedServices(contract, serviceKey).ToList();
            }
        }
        catch (Exception activationException)
        {
            findings.Add(DescribeActivationFailure(input, contract, serviceKey, activationException));
        }
    }

    /// <summary>
    /// Says what failed and who contributed to it, without claiming more than the records support.
    /// </summary>
    /// <remarks>
    /// One resolve of a group cannot say which descriptor in it failed, so no finding names a single
    /// offender on that evidence. Even where one plugin contributed every recorded descriptor in the
    /// group, the message says that plugin contributed the group rather than that it caused the
    /// failure: an activation failure can originate in a dependency no plugin registered. The count of
    /// descriptors nobody contributed is read from the snapshot taken when composition finished, and the
    /// message says so, because a host goes on registering after that.
    /// </remarks>
    private static PluginAuditFinding DescribeActivationFailure(
        PluginAuditInput input,
        Type contract,
        object? serviceKey,
        Exception activationException
    )
    {
        List<string> contributors = [];
        int attributed = 0;
        HashSet<ServiceDescriptor> attributedDescriptors = new(ReferenceEqualityComparer.Instance);

        foreach (PluginContributionRecord record in input.Records)
        {
            List<ServiceDescriptor> inGroup =
            [
                .. record.Additions.Where(descriptor => IsInGroup(descriptor, contract, serviceKey)),
            ];

            if (inGroup.Count == 0)
            {
                continue;
            }

            contributors.Add(record.PluginName);
            attributed += inGroup.Count;

            foreach (ServiceDescriptor descriptor in inGroup)
            {
                attributedDescriptors.Add(descriptor);
            }
        }

        int unattributed = input.DescriptorsAfterContribution.Count(descriptor =>
            IsInGroup(descriptor, contract, serviceKey) && !attributedDescriptors.Contains(descriptor)
        );

        string group =
            $"'{TypeNameOf(contract)}'"
            + (
                serviceKey is null
                    ? " with no service key"
                    : $" under service key '{PluginDiagnosticText.Quote(serviceKey.ToString())}'"
            );

        string attribution = contributors.Count switch
        {
            0 => "No plugin contributed a descriptor to that group, so this failure is not attributable "
                + "to a plugin.",
            _ when contributors.Count == 1 && unattributed == 0 =>
                $"Plugin '{PluginDiagnosticText.Quote(contributors[0])}' contributed all {attributed} "
                    + "descriptor(s) recorded in that group. The failure may still originate in a "
                    + "dependency the plugin did not register, so this names the contributor rather than "
                    + "the cause.",
            _ => $"{DescribePlugins(contributors)} contributed {attributed} descriptor(s) to that group, "
                + $"and {unattributed} descriptor(s) in it were contributed by no plugin, as recorded "
                + "when service composition finished. The group could not be activated and attribution "
                + "to a single plugin is not available from one resolution.",
        };

        return new PluginAuditFinding(
            PluginAuditFailure.DeclaredContractRegistrationNotActivatable,
            contributors,
            contract,
            $"resolving the registrations for the plugin contract {group} failed, so at least one of "
                + "them cannot be constructed as registered. "
                + attribution
                + $" Underlying activation exception: {PluginDiagnosticText.Quote(activationException.GetType().FullName)}: "
                + PluginDiagnosticText.Quote(activationException.Message),
            activationException
        );
    }

    private static bool IsInGroup(ServiceDescriptor descriptor, Type contract, object? serviceKey)
    {
        if (descriptor.ServiceType != contract)
        {
            return false;
        }

        return serviceKey is null
            ? !descriptor.IsKeyedService
            : descriptor.IsKeyedService && Equals(descriptor.ServiceKey, serviceKey);
    }

    private static string DescribePlugins(IReadOnlyList<string> pluginNames) =>
        string.Join(", ", pluginNames.Select(name => $"plugin '{PluginDiagnosticText.Quote(name)}'"));

    private static string TypeNameOf(Type serviceType) =>
        PluginDiagnosticText.Quote(serviceType.FullName ?? serviceType.Name);
}
