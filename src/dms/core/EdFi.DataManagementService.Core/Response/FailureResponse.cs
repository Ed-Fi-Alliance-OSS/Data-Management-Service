// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Response;

/// <summary>
/// Failure response with error lists
/// </summary>
internal static class FailureResponse
{
    private static readonly string _typePrefix = "urn:ed-fi:api";
    private static readonly string _badRequestTypePrefix = $"{_typePrefix}:bad-request";
    private static readonly string _unauthorizedType = $"{_typePrefix}:unauthorized";
    private static readonly string _gatewayType = $"{_typePrefix}:bad-gateway";
    private static readonly string _dataConflictTypePrefix = $"{_typePrefix}:data-conflict";
    private static readonly string _keyChangeNotSupported =
        $"{_badRequestTypePrefix}:data-validation-failed:key-change-not-supported";
    private static readonly string _methodNotAllowed = $"{_typePrefix}:method-not-allowed";
    private static readonly string _forbiddenType = $"{_typePrefix}:security:authorization";
    private static readonly string _tagMismatchRequestTypePrefix = $"{_typePrefix}:optimistic-lock-failed";

    private static JsonObject CreateBaseJsonObject(
        string detail,
        string type,
        string title,
        int status,
        string correlationId,
        Dictionary<string, string[]>? validationErrors = null,
        string[]? errors = null
    )
    {
        return new JsonObject
        {
            ["detail"] = detail,
            ["type"] = type,
            ["title"] = title,
            ["status"] = status,
            ["correlationId"] = correlationId,
            ["validationErrors"] =
                validationErrors != null
                    ? JsonSerializer.SerializeToNode(validationErrors, SerializerOptions)
                    : new JsonObject(),
            ["errors"] =
                errors != null ? JsonSerializer.SerializeToNode(errors, SerializerOptions) : new JsonArray(),
        };
    }

    public static JsonNode ForDataValidation(
        string detail,
        TraceId traceId,
        Dictionary<string, string[]> validationErrors,
        string[] errors
    ) =>
        CreateBaseJsonObject(
            detail,
            type: $"{_badRequestTypePrefix}:data-validation-failed",
            title: "Data Validation Failed",
            status: 400,
            correlationId: traceId.Value,
            validationErrors,
            errors
        );

    public static JsonNode ForBadRequest(
        string detail,
        TraceId traceId,
        Dictionary<string, string[]> validationErrors,
        string[] errors
    ) =>
        CreateBaseJsonObject(
            detail,
            type: _badRequestTypePrefix,
            title: "Bad Request",
            status: 400,
            correlationId: traceId.Value,
            validationErrors: validationErrors,
            errors: errors
        );

    public static JsonNode ForETagMisMatch(string detail, TraceId traceId, string[] errors) =>
        CreateBaseJsonObject(
            detail,
            type: _tagMismatchRequestTypePrefix,
            title: "Optimistic Lock Failed",
            status: 412,
            correlationId: traceId.Value,
            errors: errors
        );

    public static JsonNode ForNotFound(string detail, TraceId traceId) =>
        CreateBaseJsonObject(
            detail,
            type: $"{_typePrefix}:not-found",
            title: "Not Found",
            status: 404,
            correlationId: traceId.Value,
            validationErrors: [],
            errors: []
        );

    public static JsonNode ForIdentityConflict(string[]? errors, TraceId traceId) =>
        CreateBaseJsonObject(
            detail: "The identifying value(s) of the item are the same as another item that already exists.",
            type: $"{_typePrefix}:identity-conflict",
            title: "Identifying Values Are Not Unique",
            status: 409,
            correlationId: traceId.Value,
            validationErrors: [],
            errors
        );

    public static JsonNode ForDataConflict(string[] dependentItemNames, TraceId traceId)
    {
        return CreateBaseJsonObject(
            detail: $"The requested action cannot be performed because this item is referenced by existing {string.Join(", ", dependentItemNames)} item(s).",
            type: $"{_dataConflictTypePrefix}:dependent-item-exists",
            title: "Dependent Item Exists",
            status: 409,
            correlationId: traceId.Value,
            validationErrors: [],
            errors: []
        );
    }

    public static JsonNode ForInvalidReferences(ResourceName[] resourceNames, TraceId traceId)
    {
        string resources = string.Join(", ", resourceNames.Select(x => x.Value));
        return CreateBaseJsonObject(
            detail: $"The referenced {resources} item(s) do not exist.",
            type: $"{_dataConflictTypePrefix}:unresolved-reference",
            title: "Unresolved Reference",
            status: 409,
            correlationId: traceId.Value
        );
    }

    public static JsonNode ForImmutableIdentity(string detail, TraceId traceId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _keyChangeNotSupported,
            title: "Key Change Not Supported",
            status: 400,
            correlationId: traceId.Value,
            validationErrors: [],
            errors: []
        );

    public static JsonNode ForMethodNotAllowed(string[] errors, TraceId traceId) =>
        CreateBaseJsonObject(
            detail: "The request construction was invalid.",
            type: _methodNotAllowed,
            title: "Method Not Allowed",
            status: 405,
            correlationId: traceId.Value,
            validationErrors: [],
            errors
        );

    public static JsonNode ForUnauthorized(TraceId traceId, string error, string description) =>
        CreateBaseJsonObject(
            detail: description,
            type: _unauthorizedType,
            title: error,
            status: 401,
            correlationId: traceId.Value,
            validationErrors: [],
            errors: []
        );

    public static JsonNode ForForbidden(
        TraceId traceId,
        string[] errors,
        string typeExtension = "",
        string[]? hints = null
    )
    {
        var detail = "Access to the resource could not be authorized.";
        if (hints?.Length > 0)
        {
            detail += " " + string.Join(" ", hints);
        }

        return CreateBaseJsonObject(
            detail: detail,
            type: $"{_forbiddenType}:{typeExtension}",
            title: "Authorization Denied",
            status: 403,
            correlationId: traceId.Value,
            validationErrors: [],
            errors: errors
        );
    }

    public static JsonNode ForGatewayError(TraceId traceId, string detail = "") =>
        CreateBaseJsonObject(
            detail,
            type: _gatewayType,
            title: "Upstream service unavailable",
            status: (int)HttpStatusCode.BadGateway,
            correlationId: traceId.Value,
            validationErrors: [],
            errors: []
        );
}
