// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Response;

/// <summary>
/// ProblemDetails factories for custom view-based authorization failures (auth.md §2.4, §2.7, §2.8).
/// </summary>
/// <remarks>
/// <para>
/// §2.4 deliberately carries the bare <c>urn:ed-fi:api:security:authorization</c> type and says nothing about
/// education organization claims: a custom view need not involve them at all, which is exactly why auth.md
/// separates it from the §2.3 relationship wording.
/// </para>
/// <para>
/// The hint is appended to every case rather than only to §2.4. auth.md describes hints as applying when a
/// view-based check fails, and <see cref="RelationshipAuthorizationProblemDetails"/> already appends them to
/// its uninitialized and missing-element cases, so doing otherwise would make two sibling formatters disagree
/// about the same failure family.
/// </para>
/// </remarks>
public static class CustomViewAuthorizationFailureResponse
{
    private const string AuthorizationType = "urn:ed-fi:api:security:authorization";
    private const string CustomViewTypePrefix = $"{AuthorizationType}:custom-view";
    private const string ForbiddenTitle = "Authorization Denied";
    private const string BaseDetail = "Access to the requested data could not be authorized.";

    public static JsonNode ForFailure(CustomViewAuthorizationFailure failure, TraceId traceId)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // Every planned check names at least one securable element: the terminal reference's identity paths,
        // else the basis resource's own identity, else the basis resource name. An empty list therefore means
        // the planner's contract was violated upstream, and no wording could describe the denial.
        if (failure.ReadableSecurableElements.Length == 0)
        {
            throw new ArgumentException(
                "Custom view authorization failures must name at least one readable securable element.",
                nameof(failure)
            );
        }

        return failure.FailureKind switch
        {
            CustomViewAuthorizationFailureKind.NoMatchingRow => ForNoMatchingRow(failure, traceId),
            CustomViewAuthorizationFailureKind.StoredValueUninitialized => ForStoredValueUninitialized(
                failure,
                traceId
            ),
            CustomViewAuthorizationFailureKind.ProposedValueMissing => ForProposedValueMissing(
                failure,
                traceId
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(failure),
                failure.FailureKind,
                "Unsupported custom view authorization failure kind."
            ),
        };
    }

    private static JsonNode ForNoMatchingRow(CustomViewAuthorizationFailure failure, TraceId traceId)
    {
        var readableNames = failure.ReadableSecurableElements;
        var valueWord = SelectValueWord(failure.ValueSource);
        var error =
            readableNames.Length == 1
                ? $"The caller is not authorized to perform the requested operation on the item based on the {valueWord} value of the '{readableNames[0]}' property of the item."
                : $"The caller is not authorized to perform the requested operation on the item based on the {valueWord} values of one or more of the following properties of the item: {FormatNameList(readableNames)}.";

        return CreateResponse(AuthorizationType, AppendHint(BaseDetail, failure.Hint), [error], traceId);
    }

    private static JsonNode ForStoredValueUninitialized(
        CustomViewAuthorizationFailure failure,
        TraceId traceId
    )
    {
        var readableNames = failure.ReadableSecurableElements;
        var detail =
            readableNames.Length == 1
                ? $"{BaseDetail} The existing '{readableNames[0]}' value is required for authorization purposes."
                : $"{BaseDetail} The existing values of one or more of the following properties are required for authorization purposes: {FormatNameList(readableNames)}.";

        return CreateResponse(
            $"{CustomViewTypePrefix}:invalid-data:element-uninitialized",
            AppendHint(detail, failure.Hint),
            [
                $"The existing resource item is inaccessible to clients using the '{failure.StrategyName}' authorization strategy.",
            ],
            traceId
        );
    }

    private static JsonNode ForProposedValueMissing(CustomViewAuthorizationFailure failure, TraceId traceId)
    {
        var readableNames = failure.ReadableSecurableElements;
        var detail =
            readableNames.Length == 1
                ? $"{BaseDetail} The '{readableNames[0]}' value is required for authorization purposes."
                : $"{BaseDetail} The values of one or more of the following properties are required for authorization purposes: {FormatNameList(readableNames)}.";

        return CreateResponse(
            $"{CustomViewTypePrefix}:access-denied:element-required",
            AppendHint(detail, failure.Hint),
            [],
            traceId
        );
    }

    private static JsonNode CreateResponse(string type, string detail, string[] errors, TraceId traceId) =>
        FailureResponse.CreateBaseJsonObject(
            detail: detail,
            type: type,
            title: ForbiddenTitle,
            status: 403,
            correlationId: traceId.Value,
            validationErrors: [],
            errors: errors
        );

    private static string SelectValueWord(CustomViewAuthorizationFailureValueSource valueSource) =>
        valueSource switch
        {
            CustomViewAuthorizationFailureValueSource.Stored => "existing",
            CustomViewAuthorizationFailureValueSource.Proposed => "proposed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(valueSource),
                valueSource,
                "Unsupported custom view authorization failure value source."
            ),
        };

    /// <summary>
    /// Appends the hint sentence. The hint arrives without a <c>Hint:</c> prefix — the same convention
    /// <see cref="RelationshipAuthorizationProblemDetails"/> normalizes to — and this is where that prefix is
    /// supplied.
    /// </summary>
    private static string AppendHint(string detail, string? hint) =>
        string.IsNullOrWhiteSpace(hint) ? detail : $"{detail} Hint: {hint.Trim()}";

    private static string FormatNameList(string[] readableNames) =>
        string.Join(", ", readableNames.Select(static readableName => $"'{readableName}'"));
}
