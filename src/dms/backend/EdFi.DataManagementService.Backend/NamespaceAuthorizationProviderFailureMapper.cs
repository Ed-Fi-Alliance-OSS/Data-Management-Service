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
            // An undecodable payload the dispatcher recognized as another family's is not ours to report,
            // even though nothing in it can be decoded. Its discriminator is the only trustworthy thing
            // left in it, and it names the family whose statement raised the abort — so that family's
            // mapper owns the diagnostic. Without this, a command carrying a namespace statement beside a
            // custom-view, relationship, or ownership one files that family's defect under NamespaceBased:
            // the composite classifier reaches this arm before the relationship one, so a malformed
            // relationship or custom-view payload never gets back to its owner.
            //
            // Written as "any recognized family that is not ours" rather than as a list of the three
            // foreign families on purpose. A list would have to be extended whenever a family is added,
            // and an omission does not fail a build — it silently misattributes that family's malformed
            // payloads, which is the defect this arm exists to prevent.
            RelationalAuthorizationAuth1DispatchResult.InvalidPayload
            {
                RecognizedFamily: not null and not RelationalAuthorizationAuth1PayloadFamily.Namespace
            } => string.Empty,
            // A malformed namespace payload, or one leading with no known discriminator at all. Both are
            // ours: the first by its own discriminator, the second because no family owns it, and dropping
            // it would lose the diagnostic entirely rather than route it somewhere better.
            RelationalAuthorizationAuth1DispatchResult.InvalidPayload =>
                AuthorizationSecurityConfigurationDiagnostics.NamespaceInvalidAuth1Payload,
            RelationalAuthorizationAuth1DispatchResult.Namespace { Payload: var payload }
                when IsInvalidStaleStoredTargetPayload(payload, plannedCheckValueSources) =>
                AuthorizationSecurityConfigurationDiagnostics.NamespaceInvalidStaleTargetPayload,
            RelationalAuthorizationAuth1DispatchResult.Namespace =>
                AuthorizationSecurityConfigurationDiagnostics.NamespaceAuth1PayloadMappingFailed,
            // A payload belonging to another AUTH1 family shares the transport but is not ours to
            // classify. Yield so the codec that owns the discriminator reports it. Without this,
            // a command carrying both namespace and custom-view statements would turn a custom-view
            // 403 into a namespace invalid-metadata 500, because the catch-all below claims anything
            // it does not recognize.
            RelationalAuthorizationAuth1DispatchResult.Relationship => string.Empty,
            RelationalAuthorizationAuth1DispatchResult.CustomView => string.Empty,
            RelationalAuthorizationAuth1DispatchResult.Ownership => string.Empty,
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
