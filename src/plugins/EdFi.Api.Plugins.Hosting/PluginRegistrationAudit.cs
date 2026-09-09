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
/// records and the host's contract registry alone, so a collection that is already wrong is never
/// activated: activating registrations that a static check has already refused would run third-party
/// constructors on a candidate the host has decided not to accept.
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
    /// A declared contract registered under the wildcard service key cannot be activated at startup.
    /// </summary>
    /// <remarks>
    /// Measured on net10.0: no enumerable resolve reaches a wildcard registration. Asking for the
    /// wildcard key returns the concrete-keyed registrations and never the wildcard itself, and asking
    /// for a concrete key enumerably returns nothing for it either. Only a single-service resolve with
    /// some concrete key reaches one, and the host holds no key to supply. So the startup activation
    /// this design requires of every declared-contract registration cannot be performed for this one
    /// shape, and it is refused rather than passed over in silence. This says nothing about keyed
    /// registrations in general: a declared contract under a concrete key is activated and supported,
    /// and a wildcard registration of anything that is not a declared contract is ordinary permitted
    /// work that nothing here inspects.
    /// </remarks>
    private static void AuditWildcardKeyedContracts(
        PluginAuditInput input,
        HashSet<Type> declaredContracts,
        List<PluginAuditFinding> findings
    )
    {
        foreach (PluginContributionRecord record in input.Records)
        {
            HashSet<Type> reported = [];

            foreach (ServiceDescriptor descriptor in record.Additions)
            {
                if (
                    !descriptor.IsKeyedService
                    || !ReferenceEquals(descriptor.ServiceKey, KeyedService.AnyKey)
                    || !declaredContracts.Contains(descriptor.ServiceType)
                    || !reported.Add(descriptor.ServiceType)
                )
                {
                    continue;
                }

                findings.Add(
                    new PluginAuditFinding(
                        PluginAuditFailure.DeclaredContractRegisteredUnderWildcardKey,
                        [record.PluginName],
                        descriptor.ServiceType,
                        $"plugin '{PluginDiagnosticText.Quote(record.PluginName)}' registered the plugin "
                            + $"contract '{TypeNameOf(descriptor.ServiceType)}' under the wildcard service "
                            + "key. Every declared-contract registration is resolved once at startup, and "
                            + "a wildcard registration is reached only by resolving some concrete key, "
                            + "which the host does not hold for it. Register the contract without a key, "
                            + "or under a concrete key the host is given."
                    )
                );
            }
        }
    }

    /// <summary>
    /// A plugin whose hook ran and registered no declared contract contributed nothing the host will
    /// call.
    /// </summary>
    /// <remarks>
    /// The rule the design states is a conjunction over both composition phases: no declared contract
    /// <em>and</em> no configuration source. Phase A does not exist yet, so only the first term is
    /// live here; the second arrives with the story that adds that phase, and this check has to gain it
    /// then rather than be read as already complete.
    /// </remarks>
    private static void AuditContractsRegistered(
        PluginAuditInput input,
        HashSet<Type> declaredContracts,
        List<PluginAuditFinding> findings
    )
    {
        foreach (PluginContributionRecord record in input.Records)
        {
            if (record.Additions.Any(descriptor => declaredContracts.Contains(descriptor.ServiceType)))
            {
                continue;
            }

            IEnumerable<string> registered = record
                .Additions.Select(descriptor => TypeNameOf(descriptor.ServiceType))
                .Distinct(StringComparer.Ordinal);

            string registeredList = string.Join(", ", registered);

            findings.Add(
                new PluginAuditFinding(
                    PluginAuditFailure.NoDeclaredContractRegistered,
                    [record.PluginName],
                    contract: null,
                    $"plugin '{PluginDiagnosticText.Quote(record.PluginName)}' registered no plugin "
                        + "contract this host declares, so nothing it contributed will ever be called. It "
                        + "registered: "
                        + (registeredList.Length == 0 ? "nothing at all" : registeredList)
                        + ". The likeliest cause is a plugin allowlisted on the wrong host; a claim on a "
                        + "replace-cardinality contract made with TryAdd also lands here, because such a "
                        + "call declines silently and adds nothing."
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
    /// The unit is a group: the unkeyed registrations for a contract, resolved with one
    /// <c>GetServices</c>, and the registrations under each distinct concrete key the contract is
    /// registered under by anyone, host included, resolved with one <c>GetKeyedServices</c> each. A
    /// group holding both a host descriptor and a plugin's under the same key is one group and one
    /// resolve. Measured on net10.0, those groups are disjoint
    /// and each resolve activates every descriptor in its own group exactly once, so nothing is
    /// activated twice. The wildcard key is never resolved, for the reason
    /// <see cref="AuditWildcardKeyedContracts"/> records.
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
    /// The distinct concrete service keys a contract is registered under, from every source, in the
    /// order they were registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the collection as it stood when composition finished <em>and</em> from what the
    /// per-hook comparisons attributed to plugins, because neither alone is the whole set. The snapshot
    /// is what carries a keyed registration the <em>host</em> made: it belongs to no plugin's record,
    /// and a set derived from the records alone would form no group for its key and never resolve it,
    /// so an unconstructible host keyed registration would pass a check that refuses the equivalent
    /// unkeyed one. The records then add a key whose descriptor a later plugin removed, which is
    /// permitted for a descriptor that is neither host-owned nor logging, and which is therefore absent
    /// from the snapshot.
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
