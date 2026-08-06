// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

internal static class CustomViewAuthorizationTerminalOrdering
{
    public static IReadOnlyList<SupportedCustomViewAuthorizationStrategy> CustomViewsBeforeTerminal(
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        int terminalRawConfiguredIndex
    )
    {
        ArgumentNullException.ThrowIfNull(customViewStrategies);

        return
        [
            .. customViewStrategies.Where(strategy =>
                strategy.ConfiguredStrategy.RawConfiguredIndex < terminalRawConfiguredIndex
            ),
        ];
    }

    /// <summary>
    /// The lowest <c>RawConfiguredIndex</c> among <paramref name="knownButNotEnabledStrategies"/>, or
    /// <see cref="int.MaxValue"/> when there are none. A known-but-not-enabled strategy such as
    /// <c>OwnershipBased</c> is an AND term like a custom view, so its 501 terminal must not be preceded by
    /// validating a custom view configured after it. With no such strategy the sentinel keeps every custom
    /// view eligible.
    /// </summary>
    public static int EarliestKnownButNotEnabledRawConfiguredIndex(
        IReadOnlyList<KnownButNotEnabledRelationshipAuthorizationStrategy> knownButNotEnabledStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(knownButNotEnabledStrategies);

        return knownButNotEnabledStrategies
            .Select(static strategy => strategy.ConfiguredStrategy.RawConfiguredIndex)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
    }

    /// <summary>
    /// The planned custom-view checks configured strictly before <paramref name="terminalRawConfiguredIndex"/>.
    /// Custom views are AND filters that execute in CMS-configured order, so only these may be validated
    /// ahead of a terminal at that index.
    /// </summary>
    public static IReadOnlyList<CustomViewAuthorizationCheckSpec> ChecksBeforeTerminal(
        IReadOnlyList<CustomViewAuthorizationCheckSpec> checks,
        int terminalRawConfiguredIndex
    )
    {
        ArgumentNullException.ThrowIfNull(checks);

        return
        [
            .. checks.Where(check =>
                check.ConfiguredStrategy.RawConfiguredIndex < terminalRawConfiguredIndex
            ),
        ];
    }
}
