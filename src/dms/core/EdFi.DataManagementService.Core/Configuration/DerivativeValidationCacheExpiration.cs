// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Turns the configured derivative validation cache expiration into the one DMS actually uses.
/// </summary>
/// <remarks>
/// Resolution is separate from the setting itself so an operator gets a bounded, explained value
/// rather than whatever was typed: a verdict cached for a day would outlive the derivative database it
/// describes, and a verdict cached for a second would re-probe every request.
/// </remarks>
internal static class DerivativeValidationCacheExpiration
{
    /// <summary>Used when the setting is absent, and when a configured value is not usable.</summary>
    public const int Default = 600;

    /// <summary>The shortest accepted expiration. Below this, every request re-validates.</summary>
    public const int Minimum = 1;

    /// <summary>The longest accepted expiration: one hour.</summary>
    public const int Maximum = 3600;

    /// <summary>
    /// The expiration to use, and the warning an operator needs to see if what they configured is not
    /// what will happen.
    /// </summary>
    /// <param name="configuredValue">
    /// The configured value, or null when the setting is absent from configuration entirely. Absence
    /// and an explicitly configured value are different: absence is normal and silent, while a
    /// configured value DMS cannot honor is something the operator should be told about.
    /// </param>
    /// <returns>
    /// The effective number of seconds, and a warning message naming both the configured and the
    /// effective value, or null when there is nothing to warn about.
    /// </returns>
    public static (int EffectiveSeconds, string? Warning) Resolve(int? configuredValue)
    {
        if (configuredValue is not int configured)
        {
            return (Default, null);
        }

        if (configured < Minimum)
        {
            // Deliberately not "never expire", which is what a non-positive DataStoreCacheExpiration-
            // Seconds means. The warning says so, because an operator carrying that convention over
            // would otherwise read silence as agreement.
            return (
                Default,
                Warning(
                    configured,
                    Default,
                    "a non-positive derivative validation cache expiration does not disable expiration"
                )
            );
        }

        if (configured > Maximum)
        {
            return (Maximum, Warning(configured, Maximum, $"the maximum is {Maximum} seconds"));
        }

        return (configured, null);
    }

    /// <summary>
    /// The resolved expiration, further bounded by how long the data store configuration it was
    /// derived from is itself trusted.
    /// </summary>
    /// <remarks>
    /// A derivative's connection string comes from the data store cache. Caching a verdict about it for
    /// longer than that configuration survives would let a verdict outlive the connection string it was
    /// reached for. The bound applies only when the data store cache actually expires: with refresh
    /// disabled, or with a non-positive expiration, that configuration is held until an explicit
    /// reload, so there is no shorter lifetime to bound by and the resolved value stands. Either way
    /// the result is bounded, because <paramref name="resolvedSeconds" /> already is.
    /// </remarks>
    public static TimeSpan Effective(int resolvedSeconds, CacheSettings cacheSettings)
    {
        ArgumentNullException.ThrowIfNull(cacheSettings);

        bool dataStoreCacheExpires =
            cacheSettings.DataStoreCacheRefreshEnabled && cacheSettings.DataStoreCacheExpirationSeconds > 0;

        int seconds = dataStoreCacheExpires
            ? Math.Min(resolvedSeconds, cacheSettings.DataStoreCacheExpirationSeconds)
            : resolvedSeconds;

        return TimeSpan.FromSeconds(seconds);
    }

    private static string Warning(int configured, int effective, string reason) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "CacheSettings:DerivativeValidationCacheExpirationSeconds is configured as {0}, which is "
                + "outside the accepted range {1} to {2}; using {3} seconds instead because {4}.",
            configured,
            Minimum,
            Maximum,
            effective,
            reason
        );
}
