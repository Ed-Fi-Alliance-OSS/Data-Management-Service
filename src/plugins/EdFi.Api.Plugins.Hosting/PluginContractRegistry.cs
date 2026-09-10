// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.ObjectModel;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// One plugin contract the host declares, with the cardinality the host holds for it.
/// </summary>
/// <param name="Contract">
/// The service type a plugin registers an implementation of. A closed type: see
/// <see cref="PluginContractRegistry"/> for why an open one is refused.
/// </param>
/// <param name="Cardinality">How many implementations the host accepts.</param>
public sealed record PluginContractEntry(Type Contract, Cardinality Cardinality);

/// <summary>
/// The plugin contracts a host declares. Supplied by the host, never read from configuration or from
/// a plugin.
/// </summary>
/// <remarks>
/// <para>
/// This is the closed list the guard reasons about: it decides which registrations are contracts
/// rather than a plugin overreaching, which contracts a second claim is fatal on, whether a plugin
/// contributed anything the host will ever call, and which registrations the activation probe
/// resolves. Because it is host-supplied, a test can declare a contract of its own and exercise every
/// one of those without a real contract existing yet.
/// </para>
/// <para>
/// Each host supplies its own instance. The instance holding the Data Management Service's contracts
/// belongs on the DMS side of the seam rather than here, because a DMS-specific value in a
/// host-agnostic assembly is a value the Configuration Service would inherit and could not use.
/// </para>
/// </remarks>
public sealed class PluginContractRegistry
{
    /// <summary>
    /// The assembly declaring <see cref="EdFiApiPlugin"/>, which is in
    /// <see cref="ContractAssemblyNames"/> unconditionally.
    /// </summary>
    /// <remarks>
    /// Every plugin references it by construction, so it is the one assembly the newer-plugin-on-older-host
    /// preflight must always check. A registry with no entries still yields it, so the base contract
    /// cannot drop out of that check by way of a host that declares nothing else.
    /// </remarks>
    private static readonly string PluginContractAssemblyName =
        typeof(EdFiApiPlugin).Assembly.GetName().Name
        ?? throw new InvalidOperationException(
            "the assembly declaring EdFiApiPlugin has no simple name, so no contract assembly set can be derived"
        );

    /// <summary>
    /// Creates a registry over the given entries, in the order the host wrote them.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is null, names no contract type, names a contract carrying generic parameters, names a
    /// contract whose assembly has no simple name, or repeats a contract another entry already names.
    /// Each of those is a mistake in host code, so it is refused where the host makes it rather than
    /// where the guard later reads it.
    /// </exception>
    public PluginContractRegistry(IReadOnlyList<PluginContractEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // Copied rather than held, and exposed through a collection that refuses mutation. The
        // assembly names below are derived once, so a caller who could still add an entry afterwards
        // would leave the entries and the derived names describing different sets.
        List<PluginContractEntry> copied = [.. entries];
        List<string> assemblyNames = [PluginContractAssemblyName];
        HashSet<string> seenAssemblyNames = new(assemblyNames, StringComparer.Ordinal);
        HashSet<Type> seenContracts = [];

        for (int index = 0; index < copied.Count; index++)
        {
            PluginContractEntry entry = copied[index];

            if (entry is null)
            {
                throw new ArgumentException(
                    $"entry {index} of the plugin contract registry is null",
                    nameof(entries)
                );
            }

            // Non-nullable by annotation and null anyway is reachable: a host is ordinary code and
            // annotations do not constrain it at run time.
            if (entry.Contract is null)
            {
                throw new ArgumentException(
                    $"entry {index} of the plugin contract registry names no contract type",
                    nameof(entries)
                );
            }

            // An open contract is refused here rather than skipped by the activation probe. Every
            // declared-contract registration is resolved once at startup, and there is no type
            // argument the host could invent to close one, so a registry that admitted an open
            // contract would turn that requirement into a silent exemption.
            if (entry.Contract.ContainsGenericParameters)
            {
                throw new ArgumentException(
                    $"the plugin contract '{TypeNameOf(entry.Contract)}' carries generic parameters. A "
                        + "declared contract is resolved at startup and nothing can close an open type, "
                        + "so declare the closed contract the host actually resolves",
                    nameof(entries)
                );
            }

            if (!seenContracts.Add(entry.Contract))
            {
                throw new ArgumentException(
                    $"the plugin contract '{TypeNameOf(entry.Contract)}' appears more than once in the "
                        + "plugin contract registry, so its cardinality is ambiguous",
                    nameof(entries)
                );
            }

            // The assembly name and not the package id. An assembly reference carries a simple
            // assembly name, so a package id in this set would match nothing and would silently check
            // nothing: the contract packed as EdFi.Api.CustomValidation is declared in the assembly
            // EdFi.DataManagementService.CustomValidation, and only the second belongs here.
            string assemblyName =
                entry.Contract.Assembly.GetName().Name
                ?? throw new ArgumentException(
                    $"the assembly declaring the plugin contract '{TypeNameOf(entry.Contract)}' has no "
                        + "simple name, so it cannot be checked against a plugin's assembly references",
                    nameof(entries)
                );

            if (seenAssemblyNames.Add(assemblyName))
            {
                assemblyNames.Add(assemblyName);
            }
        }

        Entries = new ReadOnlyCollection<PluginContractEntry>(copied);
        ContractAssemblyNames = new ReadOnlyCollection<string>(assemblyNames);
    }

    /// <summary>The declared contracts, in the order the host wrote them.</summary>
    public IReadOnlyList<PluginContractEntry> Entries { get; }

    /// <summary>
    /// The simple assembly names declaring the contracts a plugin may be built against: the assembly
    /// declaring <see cref="EdFiApiPlugin"/>, always, plus the declaring assembly of every entry's
    /// contract, de-duplicated ordinally.
    /// </summary>
    /// <remarks>
    /// Derived rather than held as a second hand-written list, because a second list is a list that
    /// goes out of step: adding a contract entry adds its declaring assembly to the loader's skew
    /// preflight with no other change, which is the property that keeps a contract added by a later
    /// story covered without editing the loader.
    /// </remarks>
    public IReadOnlyList<string> ContractAssemblyNames { get; }

    private static string TypeNameOf(Type contract) =>
        PluginDiagnosticText.Quote(contract.FullName ?? contract.Name);
}
