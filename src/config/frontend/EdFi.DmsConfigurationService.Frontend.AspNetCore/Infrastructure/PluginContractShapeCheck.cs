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
/// never counted here.
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
