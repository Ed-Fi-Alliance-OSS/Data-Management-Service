// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// The two service-type tests the plugin rules key on.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsHostOwned"/> has exactly two callers, and that is a design constraint rather than a
/// coincidence: the wrapper's removal rule, which refuses a plugin editing the host's registrations,
/// and the guard's displacement check, which refuses a plugin claiming a host service type that is no
/// declared contract. The other rules do not use it at all - the replace-conflict rule counts claims
/// against the host's own contract registry, and the no-contract rule intersects a plugin's additions
/// with that same registry. A third caller, or a widening of the test itself, is the signal this has
/// become a policy engine over a party the trust model says the host cannot constrain, and the answer
/// then is the inventory record rather than another rule.
/// </para>
/// <para>
/// Neither test is an isolation property. A plugin runs with full process trust, so a party
/// determined to run its own document store can do so by means no wrapper sees. What these catch is
/// the honest mistake: an implementer who read "contribute services" as "register whatever the host
/// resolves" and would otherwise learn about it from a production defect.
/// </para>
/// </remarks>
internal static class HostOwnedServiceTypes
{
    /// <summary>
    /// The assembly-name prefixes that make a service type the host's. Compared ordinally, including
    /// the trailing separator, so an assembly named for the prefix without it does not match.
    /// </summary>
    private static readonly string[] HostAssemblyNamePrefixes =
    [
        "EdFi.DataManagementService.",
        "EdFi.DmsConfigurationService.",
    ];

    /// <summary>
    /// The service types whose descriptors carry the process's logging, written as names rather than
    /// as an assembly or namespace rule.
    /// </summary>
    /// <remarks>
    /// All four are declared in <c>Microsoft.Extensions.Logging.Abstractions</c>, and a rule over that
    /// assembly or that namespace would cover a great deal else in <c>Microsoft.Extensions.*</c> that
    /// a plugin may legitimately remove. The set is deliberately inclusive rather than minimal:
    /// measured on net10.0, neither <c>AddLogging</c> nor <c>AddSerilog</c> registers a descriptor for
    /// the non-generic <see cref="ILogger"/>, so no removal can match it in the Data Management
    /// Service as shipped. It is here anyway, because the rule is about what a plugin may remove
    /// rather than about what the host happens to register, and because a host or an earlier plugin
    /// that did register one would otherwise sit outside the carve-out for no stated reason. Do not
    /// tidy it out.
    /// </remarks>
    private static readonly Type[] LoggingPipelineServiceTypes =
    [
        typeof(ILoggerProvider),
        typeof(ILoggerFactory),
        typeof(ILogger),
    ];

    /// <summary>
    /// Whether <paramref name="serviceType"/> is declared in an assembly belonging to one of the two
    /// Ed-Fi API hosts.
    /// </summary>
    /// <remarks>
    /// The test is the declaring assembly's simple <em>assembly</em> name and nothing else - not a
    /// package id, not a namespace. The contract declared in the assembly
    /// <c>EdFi.DataManagementService.CustomValidation</c> and packed under the id
    /// <c>EdFi.Api.CustomValidation</c> is the case that makes the difference load-bearing: it matches
    /// this test, and it is admitted only because the declared-contract exemption is evaluated first.
    /// One consequence belongs in the implementer guide rather than here, and it is that a plugin must
    /// not name its own assemblies with either prefix, because every type in such an assembly is
    /// treated as the host's.
    /// <para>
    /// Read from the type's own declaring assembly rather than from its type arguments, so a framework
    /// collection closed over a host-owned type stays a framework service type. Registering one is
    /// permitted here and is the custom validation guard's business, not this one's: it runs first, at
    /// a lower startup order, and it already refuses a registration of the collection type it
    /// resolves.
    /// </para>
    /// </remarks>
    internal static bool IsHostOwned(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        // A dynamic assembly can be built with no simple name, and a service type could come from
        // one. Nothing nameless can carry a host prefix, so it is not the host's.
        if (serviceType.Assembly.GetName().Name is not { } assemblyName)
        {
            return false;
        }

        return Array.Exists(
            HostAssemblyNamePrefixes,
            prefix => assemblyName.StartsWith(prefix, StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// Whether <paramref name="serviceType"/> is one of the four logging-pipeline service types.
    /// </summary>
    /// <remarks>
    /// A plugin removing one of these silences every host log line and the plugin inventory record
    /// that would have been the only trace of what that plugin did, so removing them is refused where
    /// removing another pre-existing framework descriptor is permitted and recorded. Adding a provider
    /// is untouched by this: a plugin shipping its own sink adds one, and nothing legitimate needs to
    /// remove the host's.
    /// </remarks>
    internal static bool IsLoggingPipeline(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (
            Array.Exists(LoggingPipelineServiceTypes, loggingServiceType => serviceType == loggingServiceType)
        )
        {
            return true;
        }

        // The fourth name is an open generic, so it is matched by definition rather than by identity:
        // a descriptor can carry either ILogger<> itself or a closed ILogger<T>.
        return serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(ILogger<>);
    }
}
