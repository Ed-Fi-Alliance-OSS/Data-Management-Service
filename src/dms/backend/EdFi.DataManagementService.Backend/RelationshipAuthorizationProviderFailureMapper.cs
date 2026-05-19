// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

internal static class RelationshipAuthorizationProviderFailureMapper
{
    public static bool TryMapRelationshipAuthorizationFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs,
        IReadOnlyList<long> claimEducationOrganizationIds,
        out RelationshipAuthorizationFailure? relationshipFailure
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);
        ArgumentNullException.ThrowIfNull(checkSpecs);
        ArgumentNullException.ThrowIfNull(claimEducationOrganizationIds);

        relationshipFailure = null;

        if (
            !TryParseRelationshipAuthorizationFailure(
                dialect,
                exception,
                providerFailureExtractor,
                out var payload
            )
        )
        {
            return false;
        }

        return payload is not null
            && RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
                payload,
                checkSpecs,
                claimEducationOrganizationIds,
                out relationshipFailure
            );
    }

    public static bool IsRelationshipAuthorizationProviderFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);

        var providerFailure = providerFailureExtractor.Extract(exception);

        return RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
            dialect,
            providerFailure.ErrorCode,
            providerFailure.Message,
            out _
        );
    }

    private static bool TryParseRelationshipAuthorizationFailure(
        SqlDialect dialect,
        DbException exception,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        out RelationshipAuthorizationAuth1FailurePayload? payload
    )
    {
        var providerFailure = providerFailureExtractor.Extract(exception);

        return RelationshipAuthorizationAuth1FailurePayloadCodec.TryParseProviderFailure(
            dialect,
            providerFailure.ErrorCode,
            providerFailure.Message,
            out payload
        );
    }
}
