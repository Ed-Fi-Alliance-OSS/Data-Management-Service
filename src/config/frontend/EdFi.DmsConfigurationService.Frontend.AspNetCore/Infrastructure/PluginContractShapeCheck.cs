// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.Api.Plugins.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The registration shape the Configuration Service's plugin contracts require: singleton and
/// unkeyed.
/// </summary>
/// <remarks>
/// <para>
/// CMS-owned rather than part of the shared audit. Both declared contracts are resolved from the root
/// container by host code that expects one long-lived, unkeyed instance, so a scoped or transient
/// registration would be captured or rebuilt where the contract promises neither, and a keyed one
/// would never reach the host at all. The shared audit does not refuse these shapes, because another
/// host's contracts may legitimately allow them.
/// </para>
/// <para>
/// Only descriptors attributed to a plugin are read. The client secret hasher's host default is
/// registered once or twice depending on the identity provider, and it is in nobody's record, so it is
/// never counted here. A plugin registering an unkeyed <c>IEnumerable&lt;T&gt;</c> over a contract
/// is refused too: it replaces the collection the shared audit resolves, so the audit would activate
/// what the plugin chose rather than what host code receives.
/// </para>
/// <para>
/// <see cref="CheckResolvedInstances"/> is the exception to reading only descriptors: whether a
/// factory returns null can only be seen by resolving.
/// </para>
/// </remarks>
internal static class PluginContractShapeCheck
{
    /// <summary>
    /// The shape problems in what each plugin contributed, one message per offending descriptor.
    /// </summary>
    public static IReadOnlyList<string> Check(PluginAuditInput auditInput)
    {
        ArgumentNullException.ThrowIfNull(auditInput);

        return Check(
            auditInput.Registry,
            auditInput.Records.SelectMany(record =>
                record.Additions.Select(descriptor => (record.PluginName, descriptor))
            )
        );
    }

    /// <summary>
    /// The same check over plugin-attributed descriptors, in the order given.
    /// </summary>
    internal static IReadOnlyList<string> Check(
        PluginContractRegistry registry,
        IEnumerable<(string PluginName, ServiceDescriptor Descriptor)> attributedAdditions
    )
    {
        HashSet<Type> declaredContracts = [.. registry.Entries.Select(entry => entry.Contract)];
        List<string> findings = [];

        foreach ((string pluginName, ServiceDescriptor descriptor) in attributedAdditions)
        {
            if (
                !descriptor.IsKeyedService
                && CollectionElementOf(descriptor.ServiceType) is { } element
                && declaredContracts.Contains(element)
            )
            {
                findings.Add(
                    $"plugin '{Loggable(pluginName)}' registered "
                        + $"'IEnumerable<{Loggable(element.FullName ?? element.Name)}>' rather than the "
                        + "plugin contract itself. Registering the collection type replaces the collection "
                        + "the registration audit resolves, so the plugin has to register the contract."
                );
                continue;
            }

            if (!declaredContracts.Contains(descriptor.ServiceType))
            {
                continue;
            }

            string contract = Loggable(descriptor.ServiceType.FullName ?? descriptor.ServiceType.Name);
            string plugin = Loggable(pluginName);

            if (descriptor.IsKeyedService)
            {
                findings.Add(
                    $"plugin '{plugin}' registered the plugin contract '{contract}' under the service key "
                        + $"{KeyText(descriptor.ServiceKey)}. The Configuration Service resolves this "
                        + "contract unkeyed, so the plugin has to register it without a key."
                );
            }

            if (descriptor.Lifetime != ServiceLifetime.Singleton)
            {
                findings.Add(
                    $"plugin '{plugin}' registered the plugin contract '{contract}' as {descriptor.Lifetime}. "
                        + "The Configuration Service requires this contract to be a singleton, so the "
                        + "plugin has to register it with a singleton lifetime."
                );
            }
        }

        return findings;
    }

    /// <summary>
    /// The null instances among the declared contracts the container resolves, one message per
    /// contract.
    /// </summary>
    /// <remarks>
    /// A descriptor records that a factory exists, not what it returns, so this is the one part of the
    /// check that has to wait for the container, and it runs after the shared audit has already
    /// activated these contracts. A factory returning null satisfies that activation and every
    /// descriptor rule, and the container would then inject the null into host code that expects an
    /// instance. Every contract here is a singleton by the rule above, so resolving from the root
    /// provider yields the instances host code will receive. The host default is never null, so a
    /// null has to come from a plugin; the message names the plugins whose records hold a
    /// registration of that contract, without claiming which one returned it.
    /// </remarks>
    public static IReadOnlyList<string> CheckResolvedInstances(
        PluginAuditInput auditInput,
        IServiceProvider services
    )
    {
        ArgumentNullException.ThrowIfNull(auditInput);

        return CheckResolvedInstances(
            auditInput.Registry,
            auditInput.Records.SelectMany(record =>
                record.Additions.Select(descriptor => (record.PluginName, descriptor))
            ),
            services
        );
    }

    /// <summary>
    /// The same check, naming contributors from plugin-attributed descriptors.
    /// </summary>
    internal static IReadOnlyList<string> CheckResolvedInstances(
        PluginContractRegistry registry,
        IEnumerable<(string PluginName, ServiceDescriptor Descriptor)> attributedAdditions,
        IServiceProvider services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        List<(string PluginName, ServiceDescriptor Descriptor)> additions = [.. attributedAdditions];
        List<string> findings = [];

        foreach (Type contract in registry.Entries.Select(entry => entry.Contract))
        {
            if (!services.GetServices(contract).Any(instance => instance is null))
            {
                continue;
            }

            string contributors = string.Join(
                ", ",
                additions
                    .Where(addition =>
                        !addition.Descriptor.IsKeyedService && addition.Descriptor.ServiceType == contract
                    )
                    .Select(addition => $"'{Loggable(addition.PluginName)}'")
                    .Distinct(StringComparer.Ordinal)
            );

            findings.Add(
                $"the plugin contract '{Loggable(contract.FullName ?? contract.Name)}' "
                    + "resolved to null. A null is not an implementation: the Configuration Service would "
                    + "receive it wherever it asks for this contract. "
                    + (
                        contributors.Length == 0
                            ? "No plugin has a registration of it on record"
                            : $"Registered by plugin(s) {contributors}"
                    )
                    + "; a factory registered for a plugin contract has to return an instance."
            );
        }

        return findings;
    }

    /// <summary>
    /// The element type when <paramref name="serviceType"/> is <c>IEnumerable&lt;T&gt;</c>.
    /// </summary>
    private static Type? CollectionElementOf(Type serviceType) =>
        serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? serviceType.GetGenericArguments()[0]
            : null;

    /// <summary>
    /// A service key for a diagnostic.
    /// </summary>
    /// <remarks>
    /// A key is a plugin-supplied object whose <c>ToString</c> the host does not control, so only a
    /// string, an enum, or a primitive is rendered as a value, and anything else is named by its type.
    /// </remarks>
    internal static string KeyText(object? key) =>
        key switch
        {
            null => "null",
            string text => $"'{Loggable(text)}'",
            _ when ReferenceEquals(key, KeyedService.AnyKey) => "KeyedService.AnyKey",
            Enum => Loggable(key.ToString() ?? string.Empty),
            _ when key.GetType().IsPrimitive => Loggable(
                Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty
            ),
            _ => $"of type '{Loggable(key.GetType().FullName ?? key.GetType().Name)}'",
        };

    /// <summary>
    /// A value from outside the process, rendered so it cannot forge a log record.
    /// </summary>
    private static string Loggable(string value) =>
        string.Concat(value.Where(static character => !char.IsControl(character)));
}
