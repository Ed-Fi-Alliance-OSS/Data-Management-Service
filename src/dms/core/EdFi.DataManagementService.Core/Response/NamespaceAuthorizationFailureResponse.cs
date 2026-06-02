// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Response;

/// <summary>
/// ProblemDetails factories for namespace-based authorization failures (auth.md §2.9–2.12).
/// </summary>
/// <remarks>
/// All four 403 cases live behind a single <see cref="ForFailure"/> entry point that dispatches on
/// <see cref="NamespaceAuthorizationFailureKind"/>; both AUTH1-decoded failures and the §2.9 preflight
/// path flow through the same formatter so the response shape stays uniform.
/// </remarks>
public static class NamespaceAuthorizationFailureResponse
{
    private const string ForbiddenTypePrefix = "urn:ed-fi:api:security:authorization:namespace";
    private const string ForbiddenTitle = "Authorization Denied";

    public static JsonNode ForFailure(NamespaceAuthorizationFailure failure, TraceId traceId)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.FailureKind switch
        {
            NamespaceAuthorizationFailureKind.NoPrefixesConfigured => ForNoPrefixesConfigured(
                failure.StrategyName,
                traceId
            ),
            NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized => ForStoredUninitialized(
                failure.StrategyName,
                traceId
            ),
            NamespaceAuthorizationFailureKind.ProposedNamespaceMissing => ForProposedMissing(traceId),
            NamespaceAuthorizationFailureKind.NamespaceMismatch => ForMismatch(
                failure.ValueSource,
                failure.ConfiguredNamespacePrefixes,
                traceId
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(failure),
                failure.FailureKind,
                "Unsupported namespace authorization failure kind."
            ),
        };
    }

    private static JsonNode ForNoPrefixesConfigured(string strategyName, TraceId traceId) =>
        FailureResponse.CreateBaseJsonObject(
            detail: "There was a problem authorizing the request. The caller has not been configured correctly for accessing resources authorized by Namespace.",
            type: $"{ForbiddenTypePrefix}:invalid-client:no-namespaces",
            title: ForbiddenTitle,
            status: 403,
            correlationId: traceId.Value,
            validationErrors: [],
            errors:
            [
                $"The API client has been given permissions on a resource that uses the '{strategyName}' authorization strategy but the client doesn't have any namespace prefixes assigned.",
            ]
        );

    private static JsonNode ForStoredUninitialized(string strategyName, TraceId traceId) =>
        FailureResponse.CreateBaseJsonObject(
            detail: "Access to the requested data could not be authorized. The existing 'Namespace' value has not been assigned but is required for authorization purposes.",
            type: $"{ForbiddenTypePrefix}:invalid-data:namespace-uninitialized",
            title: ForbiddenTitle,
            status: 403,
            correlationId: traceId.Value,
            validationErrors: [],
            errors:
            [
                $"The existing resource item is inaccessible to clients using the '{strategyName}' authorization strategy because the 'Namespace' value has not been assigned.",
            ]
        );

    private static JsonNode ForProposedMissing(TraceId traceId) =>
        FailureResponse.CreateBaseJsonObject(
            detail: "Access to the requested data could not be authorized. The 'Namespace' value has not been assigned but is required for authorization purposes.",
            type: $"{ForbiddenTypePrefix}:access-denied:namespace-required",
            title: ForbiddenTitle,
            status: 403,
            correlationId: traceId.Value,
            validationErrors: [],
            errors: []
        );

    private static JsonNode ForMismatch(
        NamespaceAuthorizationFailureValueSource? valueSource,
        string[] configuredNamespacePrefixes,
        TraceId traceId
    )
    {
        var existingPrefix =
            valueSource is NamespaceAuthorizationFailureValueSource.Stored ? "existing " : string.Empty;
        var prefixList = string.Join(
            ", ",
            configuredNamespacePrefixes.Select(static prefix => $"'{prefix}'")
        );
        var detail =
            $"Access to the requested data could not be authorized. The {existingPrefix}'Namespace' value of the data does not start with any of the caller's associated namespace prefixes ({prefixList}).";

        return FailureResponse.CreateBaseJsonObject(
            detail: detail,
            type: $"{ForbiddenTypePrefix}:access-denied:namespace-mismatch",
            title: ForbiddenTitle,
            status: 403,
            correlationId: traceId.Value,
            validationErrors: [],
            errors: []
        );
    }
}
