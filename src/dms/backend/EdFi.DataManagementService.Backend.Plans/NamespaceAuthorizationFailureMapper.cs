// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Maps a decoded namespace AUTH1 payload back to a cross-boundary <see cref="NamespaceAuthorizationFailure"/>.
/// </summary>
/// <remarks>
/// The mapper is the runtime-to-external translation point for namespace failures coming out of the SQL
/// batch. The §2.9 "no namespace prefixes configured" case never reaches AUTH1 and is constructed by the
/// planner directly, not here.
/// </remarks>
public static class NamespaceAuthorizationFailureMapper
{
    public static bool TryMapAuth1Failure(
        NamespaceAuthorizationAuth1FailurePayload payload,
        IReadOnlyList<NamespaceAuthorizationCheckValueSource> plannedCheckValueSources,
        IReadOnlyList<string> configuredNamespacePrefixes,
        out NamespaceAuthorizationFailure? failure
    )
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(plannedCheckValueSources);
        ArgumentNullException.ThrowIfNull(configuredNamespacePrefixes);

        failure = null;

        if (
            plannedCheckValueSources.Count == 0
            || payload.EmittedAuth1Index >= plannedCheckValueSources.Count
        )
        {
            return false;
        }

        var valueSource = plannedCheckValueSources[payload.EmittedAuth1Index];

        if (!IsFailureKindCompatibleWithValueSource(payload.FailureKind, valueSource))
        {
            return false;
        }

        failure = new NamespaceAuthorizationFailure(
            MapFailureKind(payload.FailureKind),
            MapValueSource(valueSource),
            payload.EmittedAuth1Index,
            AuthorizationStrategyNameConstants.NamespaceBased,
            [.. configuredNamespacePrefixes]
        );
        return true;
    }

    private static bool IsFailureKindCompatibleWithValueSource(
        NamespaceAuthorizationAuth1FailureKind failureKind,
        NamespaceAuthorizationCheckValueSource valueSource
    ) =>
        (failureKind, valueSource) switch
        {
            (NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch, _) => true,
            (
                NamespaceAuthorizationAuth1FailureKind.StoredNamespaceUninitialized,
                NamespaceAuthorizationCheckValueSource.Stored
            ) => true,
            (
                NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing,
                NamespaceAuthorizationCheckValueSource.Proposed
            ) => true,
            _ => false,
        };

    private static NamespaceAuthorizationFailureKind MapFailureKind(
        NamespaceAuthorizationAuth1FailureKind failureKind
    ) =>
        failureKind switch
        {
            NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch =>
                NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationAuth1FailureKind.StoredNamespaceUninitialized =>
                NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized,
            NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing =>
                NamespaceAuthorizationFailureKind.ProposedNamespaceMissing,
            _ => throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported AUTH1 namespace failure kind."
            ),
        };

    private static NamespaceAuthorizationFailureValueSource MapValueSource(
        NamespaceAuthorizationCheckValueSource valueSource
    ) =>
        valueSource switch
        {
            NamespaceAuthorizationCheckValueSource.Stored => NamespaceAuthorizationFailureValueSource.Stored,
            NamespaceAuthorizationCheckValueSource.Proposed =>
                NamespaceAuthorizationFailureValueSource.Proposed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(valueSource),
                valueSource,
                "Unsupported namespace authorization value source."
            ),
        };
}
