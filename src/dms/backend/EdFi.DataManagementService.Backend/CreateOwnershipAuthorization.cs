// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Decides, from the API client's application context alone, what a POST owes if its target resolves to a
/// create while <c>OwnershipBased</c> is planned for the Create action.
/// </summary>
/// <remarks>
/// <para>
/// A create has no stored token to authorize, so the verdict reads only the client: the row it would write is
/// stamped from <c>CreatorOwnershipTokenId</c>, and a row the client could not reach through its own
/// <c>OwnershipTokenIds</c> must not be written. No SQL is involved and none is emitted.
/// </para>
/// <para>
/// Conditions are checked in order: the defensive token cap first, as on every other ownership path, so an
/// over-limit list fails closed with the security-configuration 500 whatever its contents; then a missing
/// creator token (§2.14); then a creator token outside the client's own list (§2.13). The two denials reuse
/// the stored-token kinds and so the existing response bodies. The index is the planned check's earliest
/// configured <c>OwnershipBased</c> occurrence, so duplicate configuration yields one verdict.
/// </para>
/// <para>
/// The result is unattributed. The executor stamps the selected POST action on a security-configuration
/// failure at its boundary, as it does for every other branch result.
/// </para>
/// </remarks>
internal static class CreateOwnershipAuthorization
{
    /// <returns>
    /// The failure the create owes, or <see langword="null"/> when the create may proceed — including when no
    /// ownership check is planned.
    /// </returns>
    public static UpsertResult? Evaluate(
        SqlDialect dialect,
        OwnershipAuthorizationCheckSpec? ownershipCheck,
        RelationalAuthorizationContext authorizationContext
    )
    {
        ArgumentNullException.ThrowIfNull(authorizationContext);

        if (ownershipCheck is null)
        {
            return null;
        }

        if (
            !OwnershipTokenParameterizationPreflight.TryCreate(
                dialect,
                authorizationContext.OwnershipTokenIds,
                out _,
                out var securityConfigurationMessage,
                out var securityConfigurationDiagnostics
            )
        )
        {
            return new UpsertResult.UpsertFailureSecurityConfiguration(
                [securityConfigurationMessage],
                securityConfigurationDiagnostics
            );
        }

        if (authorizationContext.CreatorOwnershipTokenId is not { } creatorOwnershipTokenId)
        {
            return Denied(
                OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized,
                ownershipCheck
            );
        }

        return authorizationContext.OwnershipTokenIds.Contains(creatorOwnershipTokenId)
            ? null
            : Denied(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch, ownershipCheck);
    }

    private static UpsertResult Denied(
        OwnershipAuthorizationFailureKind failureKind,
        OwnershipAuthorizationCheckSpec ownershipCheck
    ) =>
        new UpsertResult.UpsertFailureOwnershipNotAuthorized(
            new OwnershipAuthorizationFailure(
                failureKind,
                ownershipCheck.RawConfiguredIndex,
                ownershipCheck.StrategyName
            )
        );
}
