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
/// Maps a provider <see cref="DbException"/> carrying a custom-view AUTH1 payload (<c>cv1|index|kind</c>)
/// back to a cross-boundary <see cref="CustomViewAuthorizationFailure"/>.
/// </summary>
/// <remarks>
/// Routes through the shared <see cref="RelationalAuthorizationAuth1Dispatcher"/>, so a relationship
/// <c>1|...</c> or namespace <c>ns1|...</c> payload sharing the same command is never claimed here — the
/// mirror of the yields those families make for <c>cv1</c>.
/// <para>
/// Every method takes the request's full planned check list, not just the batch that raised the failure. The
/// payload carries only an index and resolution is positional, so a request that emits several batches keeps
/// its indexes unique across all of them and resolves any of their payloads against that one list. Passing a
/// batch-local or reordered list would report a denial as some other check's category.
/// </para>
/// </remarks>
internal static class CustomViewAuthorizationProviderFailureMapper
{
    public static bool TryMapCustomViewAuthorizationFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> plannedChecks,
        out CustomViewAuthorizationFailure? failure
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);
        ArgumentNullException.ThrowIfNull(plannedChecks);

        failure = null;

        return TryDispatchCustomViewPayload(dialect, exception, providerFailureExtractor, out var payload)
            && payload is not null
            && CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(payload, plannedChecks, out failure);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> reports that the stored target row no longer exists. The read
    /// boundary re-resolves the target on this result so a row deleted between the unlocked lookup and the
    /// check surfaces as a 404 rather than an authorization denial.
    /// </summary>
    public static bool IsStaleStoredTargetFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> plannedChecks
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);
        ArgumentNullException.ThrowIfNull(plannedChecks);

        return TryDispatchCustomViewPayload(dialect, exception, providerFailureExtractor, out var payload)
            && payload is not null
            && CustomViewAuthorizationFailureMapper.IsStaleStoredTargetFailure(payload, plannedChecks);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> carries a custom-view AUTH1 payload that this family owns but
    /// cannot turn into a response — an out-of-range index, or a kind the indexed check's value source cannot
    /// produce. Such a payload is a security-configuration defect, not a denial, so the caller reports 500
    /// rather than inventing a 403.
    /// </summary>
    public static bool IsUnmappableCustomViewPayload(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> plannedChecks
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);
        ArgumentNullException.ThrowIfNull(plannedChecks);

        if (!TryDispatchCustomViewPayload(dialect, exception, providerFailureExtractor, out var payload))
        {
            return false;
        }

        return payload is not null
            && !CustomViewAuthorizationFailureMapper.IsStaleStoredTargetFailure(payload, plannedChecks)
            && !CustomViewAuthorizationFailureMapper.TryMapAuth1Failure(payload, plannedChecks, out _);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> carries no recognized authorization payload at all. A custom-view
    /// statement references <c>auth.{StrategyName}</c>, which is created outside the schema and can be
    /// missing, replaced, or revoked, so such a failure is attributed to the view rather than escaping as an
    /// unhandled provider error.
    /// </summary>
    public static bool IsUnrecognizedProviderFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);

        var providerFailure = providerFailureExtractor.Extract(exception);

        return !RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            dialect,
            providerFailure.ErrorCode,
            providerFailure.Message,
            out _
        );
    }

    private static bool TryDispatchCustomViewPayload(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        out CustomViewAuthorizationAuth1FailurePayload? payload
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

        if (dispatchResult is RelationalAuthorizationAuth1DispatchResult.CustomView customViewResult)
        {
            payload = customViewResult.Payload;
            return true;
        }

        return false;
    }
}
