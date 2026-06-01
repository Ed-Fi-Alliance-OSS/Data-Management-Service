// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Builders for the cross-cutting namespace authorization shapes that the read, write, and descriptor
/// pipelines all assemble: the §2.9 no-prefixes failure record and the per-value-source split of a
/// planned check list.
/// </summary>
internal static class NamespaceAuthorizationFactory
{
    /// <summary>
    /// Builds the §2.9 <c>NoPrefixesConfigured</c> failure record. <see cref="NamespaceAuthorizationFailure.ValueSource"/>
    /// and <see cref="NamespaceAuthorizationFailure.EmittedAuth1Index"/> are <see langword="null"/> because the
    /// no-prefixes path is emitted at planner/preflight time without evaluating stored or proposed data and
    /// never reaches AUTH1.
    /// </summary>
    public static NamespaceAuthorizationFailure NoPrefixesConfiguredFailure(string strategyName) =>
        new(
            NamespaceAuthorizationFailureKind.NoPrefixesConfigured,
            ValueSource: null,
            EmittedAuth1Index: null,
            strategyName,
            ConfiguredNamespacePrefixes: []
        );

    /// <summary>
    /// Filters <paramref name="namespaceChecks"/> down to the requested <paramref name="valueSource"/>,
    /// reindexes the surviving checks from zero, and packages them into a
    /// <see cref="RelationalWriteNamespaceAuthorization"/>. Returns <see langword="null"/> when no checks
    /// match the requested value source, so callers can branch on "this side has nothing to run."
    /// </summary>
    public static RelationalWriteNamespaceAuthorization? SplitByValueSource(
        IReadOnlyList<NamespaceAuthorizationCheckSpec> namespaceChecks,
        NamespaceAuthorizationCheckValueSource valueSource,
        NamespacePrefixParameterization namespacePrefixParameterization
    )
    {
        ArgumentNullException.ThrowIfNull(namespaceChecks);
        ArgumentNullException.ThrowIfNull(namespacePrefixParameterization);

        var checks = namespaceChecks
            .Where(check => check.ValueSource == valueSource)
            .Select(static (check, index) => check with { Index = index })
            .ToArray();

        return checks.Length == 0
            ? null
            : new RelationalWriteNamespaceAuthorization(checks, namespacePrefixParameterization);
    }
}
