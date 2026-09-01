// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Response;

/// <summary>
/// ProblemDetails factories for ownership-based authorization failures (auth.md §2.13, §2.14).
/// </summary>
/// <remarks>
/// <para>
/// Both cases share one <c>detail</c> sentence. That is auth.md's wording, not a copy-paste: the client is told
/// only that the item is not owned by the caller, and the distinction between a stored token that matched
/// nothing (§2.13) and a stored token that was never assigned (§2.14) is carried by <c>type</c> and by §2.14's
/// <c>errors</c> entry. Keep the shared sentence in one constant so the two cases cannot drift apart.
/// </para>
/// <para>
/// Neither body reveals an ownership token value — not the caller's and not the stored one — and neither
/// renders <see cref="OwnershipAuthorizationFailure.ConfiguredStrategyIndex"/>, which exists for log
/// traceability rather than for the client.
/// </para>
/// <para>
/// There is no preflight case here, unlike <see cref="NamespaceAuthorizationFailureResponse"/> with its §2.9
/// no-prefixes-configured response. A caller holding no ownership tokens still executes the stored-row check,
/// precisely so the response can tell §2.13 from §2.14 instead of guessing before reading the row.
/// </para>
/// </remarks>
public static class OwnershipAuthorizationFailureResponse
{
    private const string ForbiddenTypePrefix = "urn:ed-fi:api:security:authorization:ownership";
    private const string ForbiddenTitle = "Authorization Denied";

    /// <summary>
    /// The single client-facing sentence both §2.13 and §2.14 use.
    /// </summary>
    private const string NotOwnedDetail =
        "Access to the requested data could not be authorized. The item is not owned by the caller.";

    public static JsonNode ForFailure(OwnershipAuthorizationFailure failure, TraceId traceId)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.FailureKind switch
        {
            OwnershipAuthorizationFailureKind.OwnershipTokenMismatch => ForMismatch(traceId),
            OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized => ForStoredUninitialized(
                failure.StrategyName,
                traceId
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(failure),
                failure.FailureKind,
                "Unsupported ownership authorization failure kind."
            ),
        };
    }

    private static JsonNode ForMismatch(TraceId traceId) =>
        CreateResponse($"{ForbiddenTypePrefix}:access-denied:ownership-mismatch", [], traceId);

    private static JsonNode ForStoredUninitialized(string strategyName, TraceId traceId) =>
        CreateResponse(
            $"{ForbiddenTypePrefix}:invalid-data:ownership-uninitialized",
            [
                $"The existing resource item has no 'CreatedByOwnershipTokenId' value assigned and thus will never be accessible to clients using the '{strategyName}' authorization strategy.",
            ],
            traceId
        );

    private static JsonNode CreateResponse(string type, string[] errors, TraceId traceId) =>
        FailureResponse.CreateBaseJsonObject(
            detail: NotOwnedDetail,
            type: type,
            title: ForbiddenTitle,
            status: 403,
            correlationId: traceId.Value,
            validationErrors: [],
            errors: errors
        );
}
