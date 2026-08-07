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
