// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Maps a provider <see cref="DbException"/> carrying a namespace AUTH1 payload (<c>ns1|index|kind</c>)
/// back to a cross-boundary <see cref="NamespaceAuthorizationFailure"/>. Routes through the shared
/// <see cref="RelationalAuthorizationAuth1Dispatcher"/> so a relationship <c>1|...</c> payload is never
/// mistaken for a namespace failure.
/// </summary>
internal static class NamespaceAuthorizationProviderFailureMapper
{
    public static bool TryMapNamespaceAuthorizationFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        IReadOnlyList<NamespaceAuthorizationCheckValueSource> plannedCheckValueSources,
        IReadOnlyList<string> configuredNamespacePrefixes,
        out NamespaceAuthorizationFailure? failure
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);
        ArgumentNullException.ThrowIfNull(plannedCheckValueSources);
        ArgumentNullException.ThrowIfNull(configuredNamespacePrefixes);

        failure = null;

        if (!TryDispatchNamespacePayload(dialect, exception, providerFailureExtractor, out var payload))
        {
            return false;
        }

        return payload is not null
            && NamespaceAuthorizationFailureMapper.TryMapAuth1Failure(
                payload,
                plannedCheckValueSources,
                configuredNamespacePrefixes,
                out failure
            );
    }

    /// <summary>
    /// Whether <paramref name="exception"/> carries a namespace AUTH1 payload reporting that the stored
    /// target row no longer exists (<see cref="NamespaceAuthorizationAuth1FailureKind.StoredTargetMissing"/>).
    /// The executor maps this to a stale-target result so unlocked read paths re-resolve the target
    /// rather than treating the missing row as a namespace-mismatch denial.
    /// </summary>
    /// <remarks>
    /// The payload is only treated as stale when its emitted index is in range and the indexed planned
    /// check is a stored-value check — the only shape the SQL compiler ever emits the stale kind from. A
    /// malformed payload (out-of-range index, or the stale kind paired with a proposed check) returns
    /// <see langword="false"/> so it falls through to the invalid-metadata security-configuration mapping
    /// rather than being silently converted into a stale-target retry or a write conflict.
    /// </remarks>
    public static bool IsStaleStoredTargetFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        IReadOnlyList<NamespaceAuthorizationCheckValueSource> plannedCheckValueSources
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);
        ArgumentNullException.ThrowIfNull(plannedCheckValueSources);

        return TryDispatchNamespacePayload(dialect, exception, providerFailureExtractor, out var payload)
            && payload is { FailureKind: NamespaceAuthorizationAuth1FailureKind.StoredTargetMissing }
            && payload.EmittedAuth1Index < plannedCheckValueSources.Count
            && plannedCheckValueSources[payload.EmittedAuth1Index]
                is NamespaceAuthorizationCheckValueSource.Stored;
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is an AUTH1 provider failure this namespace executor should
    /// fail closed on (a namespace <c>ns1|…</c> payload — mappable or not — or any malformed/unknown
    /// AUTH1 payload). A relationship <c>1|…</c> payload returns <see langword="false"/> so it rethrows,
    /// because it should never reach the namespace-only execution path.
    /// </summary>
    public static bool IsNamespaceAuthorizationProviderFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);

        var providerFailure = providerFailureExtractor.Extract(exception);

        if (
            !RelationalAuthorizationAuth1Dispatcher.TryDispatch(
                dialect,
                providerFailure.ErrorCode,
                providerFailure.Message,
                out var dispatchResult
            )
        )
        {
            return false;
        }

        return dispatchResult
            is RelationalAuthorizationAuth1DispatchResult.Namespace
                or RelationalAuthorizationAuth1DispatchResult.InvalidPayload;
    }

    public static bool TryBuildInvalidAuthorizationFailureDiagnostics(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        IReadOnlyList<NamespaceAuthorizationCheckValueSource> plannedCheckValueSources,
        IReadOnlyList<NamespaceAuthorizationCheckSpec> checks,
        out SecurityConfigurationFailureDiagnostic[]? diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);
        ArgumentNullException.ThrowIfNull(plannedCheckValueSources);
        ArgumentNullException.ThrowIfNull(checks);

        diagnostics = null;
        var providerFailure = providerFailureExtractor.Extract(exception);

        if (
            !RelationalAuthorizationAuth1Dispatcher.TryDispatch(
                dialect,
                providerFailure.ErrorCode,
                providerFailure.Message,
                out var dispatchResult
            )
        )
        {
            return false;
        }

        string providerOrPlannerFailureKind = dispatchResult switch
        {
            RelationalAuthorizationAuth1DispatchResult.InvalidPayload =>
                AuthorizationSecurityConfigurationDiagnostics.NamespaceInvalidAuth1Payload,
            RelationalAuthorizationAuth1DispatchResult.Namespace { Payload: var payload }
                when IsInvalidStaleStoredTargetPayload(payload, plannedCheckValueSources) =>
                AuthorizationSecurityConfigurationDiagnostics.NamespaceInvalidStaleTargetPayload,
            RelationalAuthorizationAuth1DispatchResult.Namespace =>
                AuthorizationSecurityConfigurationDiagnostics.NamespaceAuth1PayloadMappingFailed,
            RelationalAuthorizationAuth1DispatchResult.Relationship => string.Empty,
            _ => AuthorizationSecurityConfigurationDiagnostics.NamespaceInvalidAuthorizationMetadata,
        };

        if (string.IsNullOrEmpty(providerOrPlannerFailureKind))
        {
            return false;
        }

        diagnostics = AuthorizationSecurityConfigurationDiagnostics.ForNamespaceAuthorizationAuth1(
            providerOrPlannerFailureKind,
            checks
        );
        return true;
    }

    private static bool TryDispatchNamespacePayload(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        out NamespaceAuthorizationAuth1FailurePayload? payload
    )
    {
        payload = null;
        var providerFailure = providerFailureExtractor.Extract(exception);

        if (
            !RelationalAuthorizationAuth1Dispatcher.TryDispatch(
                dialect,
                providerFailure.ErrorCode,
                providerFailure.Message,
                out var dispatchResult
            )
        )
        {
            return false;
        }

        if (dispatchResult is RelationalAuthorizationAuth1DispatchResult.Namespace namespaceResult)
        {
            payload = namespaceResult.Payload;
            return true;
        }

        return false;
    }

    private static bool IsInvalidStaleStoredTargetPayload(
        NamespaceAuthorizationAuth1FailurePayload payload,
        IReadOnlyList<NamespaceAuthorizationCheckValueSource> plannedCheckValueSources
    ) =>
        payload.FailureKind is NamespaceAuthorizationAuth1FailureKind.StoredTargetMissing
        && (
            payload.EmittedAuth1Index >= plannedCheckValueSources.Count
            || plannedCheckValueSources[payload.EmittedAuth1Index]
                is not NamespaceAuthorizationCheckValueSource.Stored
        );
}
