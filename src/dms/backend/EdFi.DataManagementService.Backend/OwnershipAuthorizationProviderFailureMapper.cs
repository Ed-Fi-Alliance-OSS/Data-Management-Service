// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Maps a provider <see cref="DbException"/> carrying an ownership AUTH1 payload
/// (<c>own1|configuredIndex|kind</c>) to the execution result it obliges. Routes through the shared
/// <see cref="RelationalAuthorizationAuth1Dispatcher"/> so a relationship <c>1|…</c>, namespace <c>ns1|…</c>
/// or custom-view <c>cv1|…</c> payload is never mistaken for an ownership failure.
/// </summary>
/// <remarks>
/// <para>
/// This mapper claims an AUTH1 failure only when the payload belongs to the <c>own1</c> family, including
/// the case where an <c>own1|</c>-prefixed payload is malformed and so cannot be decoded at all. Deciding
/// that by the payload's own discriminator, rather than by which statements the command happened to carry,
/// is what makes the mapper safe to consult from a co-batched path: it can never claim another family's
/// malformed payload, and another family can never legitimately claim ours.
/// </para>
/// <para>
/// The reverse direction is closed too, and independently: <c>NamespaceAuthorizationProviderFailureMapper</c>
/// yields on an <c>own1|</c>-prefixed undecodable payload rather than claiming every payload it cannot
/// identify, and <c>RelationalCompositeStoredAuthorization.TryClassifyDenial</c> consults this mapper ahead
/// of it. Either guard alone attributes a malformed ownership payload correctly; both exist so that removing
/// one does not silently file an ownership defect under <c>NamespaceBased</c>.
/// </para>
/// </remarks>
internal static class OwnershipAuthorizationProviderFailureMapper
{
    /// <param name="plannedConfiguredStrategyIndex">
    /// The configured strategy index the request's single planned ownership check was stamped with, or
    /// <see langword="null"/> when the request planned no ownership check. A payload arriving against a
    /// request that planned none is still claimed here — it can have come from no other family — and fails
    /// closed as a security-configuration failure.
    /// </param>
    /// <param name="result">
    /// The obliged execution result: a denial, a stale-target retry signal, or a security-configuration
    /// failure. Never <c>Authorized</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="exception"/> carries an ownership AUTH1 payload;
    /// <see langword="false"/> when it carries no AUTH1 payload at all, or one belonging to another family,
    /// in which case the exception is not this mapper's to answer.
    /// </returns>
    public static bool TryMapOwnershipAuthorizationFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        int? plannedConfiguredStrategyIndex,
        out OwnershipAuthorizationExecutionResult? result
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);

        result = null;
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

        switch (dispatchResult)
        {
            case RelationalAuthorizationAuth1DispatchResult.Ownership { Payload: var payload }:
                result = MapDecodedPayload(payload, plannedConfiguredStrategyIndex);
                return true;

            // An undecodable payload that still announces itself as ours. The check emitted it, so no other
            // family may answer for it, but nothing in it can be trusted to attribute a denial.
            case RelationalAuthorizationAuth1DispatchResult.InvalidPayload { RawPayload: var rawPayload }
                when IsOwnershipFamilyPayload(rawPayload):
                result = BuildInvalidAuthorizationFailure(
                    AuthorizationSecurityConfigurationDiagnostics.OwnershipAuth1PayloadMappingFailed,
                    plannedConfiguredStrategyIndex
                );
                return true;

            default:
                return false;
        }
    }

    private static OwnershipAuthorizationExecutionResult MapDecodedPayload(
        OwnershipAuthorizationAuth1FailurePayload payload,
        int? plannedConfiguredStrategyIndex
    ) =>
        OwnershipAuthorizationFailureMapper.Map(payload, plannedConfiguredStrategyIndex) switch
        {
            OwnershipAuthorizationAuth1MapResult.Denied denied =>
                new OwnershipAuthorizationExecutionResult.NotAuthorized(denied.Failure),
            OwnershipAuthorizationAuth1MapResult.StaleStoredTarget =>
                new OwnershipAuthorizationExecutionResult.StaleTarget(),
            OwnershipAuthorizationAuth1MapResult.Unmappable unmappable => BuildInvalidAuthorizationFailure(
                MapUnmappableToDiagnosticKind(unmappable.Reason, payload.FailureKind),
                plannedConfiguredStrategyIndex
            ),
            var unrecognized => throw new InvalidOperationException(
                $"Unsupported ownership AUTH1 map result '{unrecognized.GetType().Name}'."
            ),
        };

    /// <remarks>
    /// A stale-target payload raised against a request that planned no ownership check at all gets its own
    /// diagnostic kind: it is the one unmappable shape that reports the retry path emitting a check the plan
    /// does not contain, which is a different fault from a payload whose index simply is not ours.
    /// </remarks>
    private static string MapUnmappableToDiagnosticKind(
        OwnershipAuthorizationAuth1UnmappableReason reason,
        OwnershipAuthorizationAuth1FailureKind failureKind
    ) =>
        reason switch
        {
            OwnershipAuthorizationAuth1UnmappableReason.NoOwnershipCheckPlanned => failureKind
            is OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing
                ? AuthorizationSecurityConfigurationDiagnostics.OwnershipInvalidStaleTargetPayload
                : AuthorizationSecurityConfigurationDiagnostics.OwnershipAuth1PayloadMappingFailed,
            OwnershipAuthorizationAuth1UnmappableReason.ConfiguredStrategyIndexMismatch =>
                AuthorizationSecurityConfigurationDiagnostics.OwnershipAuth1PayloadMappingFailed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "Unsupported ownership AUTH1 unmappable reason."
            ),
        };

    private static OwnershipAuthorizationExecutionResult.InvalidAuthorizationFailure BuildInvalidAuthorizationFailure(
        string providerOrPlannerFailureKind,
        int? plannedConfiguredStrategyIndex
    ) =>
        new(
            OwnershipAuthorizationSecurityConfigurationMessages.InvalidAuthorizationMetadata,
            AuthorizationSecurityConfigurationDiagnostics.ForOwnershipAuthorizationAuth1(
                providerOrPlannerFailureKind,
                plannedConfiguredStrategyIndex
            )
        );

    private static bool IsOwnershipFamilyPayload(string rawPayload) =>
        rawPayload.StartsWith(
            OwnershipAuthorizationAuth1FailurePayloadCodec.PayloadDiscriminator + "|",
            StringComparison.Ordinal
        );
}
