// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.DataModel.Infrastructure;

public static class FailureResponse
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string _typePrefix = "urn:ed-fi:api";
    private static readonly string _unauthorizedType = $"{_typePrefix}:security:authentication";
    private static readonly string _forbiddenType = $"{_typePrefix}:security:authorization";
    private static readonly string _badRequestTypePrefix = $"{_typePrefix}:bad-request";
    private static readonly string _parameterValidationType = $"{_typePrefix}:bad-request:parameter";
    private static readonly string _notFoundTypePrefix = $"{_typePrefix}:not-found";
    private static readonly string _conflictTypePrefix = $"{_typePrefix}:conflict";
    private static readonly string _badGatewayTypePrefix = $"{_typePrefix}:bad-gateway";
    private static readonly string _unavailableType = $"{_typePrefix}:internal-server-error";
    private static readonly string _methodNotAllowedType = $"{_typePrefix}:method-not-allowed";
    private static readonly string _unsupportedMediaTypeType = $"{_typePrefix}:unsupported-media-type";

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
                    ? JsonSerializer.SerializeToNode(validationErrors, _serializerOptions)
                    : new JsonObject(),
            ["errors"] =
                errors != null ? JsonSerializer.SerializeToNode(errors, _serializerOptions) : new JsonArray(),
        };
    }

    public static JsonNode ForUnauthorized(
        string title,
        string detail,
        string correlationId,
        string[]? errors = null
    ) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _unauthorizedType,
            title: title,
            status: 401,
            correlationId: correlationId,
            errors: errors
        );

    public static JsonNode ForForbidden(
        string title,
        string detail,
        string correlationId,
        string[]? errors = null
    ) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _forbiddenType,
            title: title,
            status: 403,
            correlationId: correlationId,
            errors: errors
        );

    // The status defaults to 400 but is caller-overridable so that a preserved framework client-error
    // status (e.g. an unmodeled BadHttpRequestException status) can still return a machine-readable body
    // whose status member matches the HTTP status, rather than an empty response.
    public static JsonNode ForBadRequest(string detail, string correlationId, int status = 400) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _badRequestTypePrefix,
            title: "Bad Request",
            status: status,
            correlationId: correlationId,
            []
        );

    public static JsonNode ForNotFound(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _notFoundTypePrefix,
            title: "Not Found",
            status: 404,
            correlationId: correlationId,
            []
        );

    public static JsonNode ForConflict(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _conflictTypePrefix,
            title: "Conflict",
            status: 409,
            correlationId: correlationId,
            []
        );

    public static JsonNode ForMethodNotAllowed(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _methodNotAllowedType,
            title: "Method Not Allowed",
            status: 405,
            correlationId: correlationId,
            []
        );

    public static JsonNode ForUnsupportedMediaType(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _unsupportedMediaTypeType,
            title: "Unsupported Media Type",
            status: 415,
            correlationId: correlationId,
            []
        );

    public static JsonNode ForDataValidation(
        IEnumerable<ValidationFailure> validationFailures,
        string correlationId
    ) =>
        CreateBaseJsonObject(
            detail: "Data validation failed. See 'validationErrors' for details.",
            type: $"{_badRequestTypePrefix}:data",
            title: "Data Validation Failed",
            status: 400,
            correlationId: correlationId,
            validationFailures
                // Normalize before grouping so failures that map to the same JSON path merge under one key.
                .GroupBy(x => NormalizeToJsonPath(x.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage).ToArray())
        );

    // Query-parameter validation failure (400, urn:ed-fi:api:bad-request:parameter). Per the Ed-Fi Error
    // Response Knowledge Base, invalid query parameters (e.g. limit/offset) use the "Parameter Validation
    // Failed" contract with details in 'errors', distinct from request-body ("data") validation. The
    // supplied error messages must already be sanitized (no rejected value echoed); validationErrors stays
    // empty ({}) because the failures are query parameters, not request-document fields.
    public static JsonNode ForParameterValidation(string[] errors, string correlationId) =>
        CreateBaseJsonObject(
            detail: "One or more query parameters were invalid. See 'errors' for details.",
            type: _parameterValidationType,
            title: "Parameter Validation Failed",
            status: 400,
            correlationId: correlationId,
            errors: errors
        );

    public static JsonNode ForNonUniqueIdentity(
        string detail,
        string correlationId,
        string[]? errors = null
    ) =>
        CreateBaseJsonObject(
            detail: detail,
            type: $"{_conflictTypePrefix}:non-unique-identity",
            title: "Identifying Values Are Not Unique",
            status: 409,
            correlationId: correlationId,
            errors: errors
        );

    // A request referencing a resource that does not exist. Per the Ed-Fi Error Response Knowledge Base
    // this is a 409 conflict (urn:ed-fi:api:conflict:unresolved-reference), not a 400 data-validation
    // failure, so validationErrors stays empty and any supplementary detail goes in errors.
    public static JsonNode ForUnresolvedReference(
        string detail,
        string correlationId,
        string[]? errors = null
    ) =>
        CreateBaseJsonObject(
            detail: detail,
            type: $"{_conflictTypePrefix}:unresolved-reference",
            title: "Unresolved Reference",
            status: 409,
            correlationId: correlationId,
            errors: errors
        );

    // The requested action cannot be performed because the item is referenced by existing item(s), e.g.
    // deleting a resource that still has dependents (urn:ed-fi:api:conflict:dependent-item-exists, 409).
    public static JsonNode ForDependentItemExists(
        string detail,
        string correlationId,
        string[]? errors = null
    ) =>
        CreateBaseJsonObject(
            detail: detail,
            type: $"{_conflictTypePrefix}:dependent-item-exists",
            title: "Dependent Item Exists",
            status: 409,
            correlationId: correlationId,
            errors: errors
        );

    public static JsonNode ForBadGateway(string detail, string correlationId, string[]? errors = null) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _badGatewayTypePrefix,
            title: "Bad Gateway",
            status: 502,
            correlationId: correlationId,
            errors: errors
        );

    public static JsonNode ForUnknown(string correlationId) =>
        CreateBaseJsonObject(
            detail: "",
            type: _unavailableType,
            title: "Internal Server Error",
            status: 500,
            correlationId: correlationId,
            errors: []
        );

    /// <summary>
    /// Converts a FluentValidation property name into the request document's JSON path so validationErrors
    /// keys point at the submitted field (matching the wider Ed-Fi contract, e.g. "$.namespacePrefixes"
    /// rather than "NamespacePrefixes"). Each dot-separated segment's identifier is camel-cased with
    /// JsonNamingPolicy.CamelCase (so leading acronyms like "APIKey" become "apiKey", not "aPIKey") while
    /// any array-index suffix (e.g. "[0]") is preserved, then the JSON-path root "$" is prefixed. The
    /// empty/root property name maps to "$". The policy leaves snake_case and already-camel-cased names
    /// (including explicit .OverridePropertyName sources such as "claimSetName") unchanged, so the transform
    /// is idempotent: a value already in this form maps to itself.
    /// </summary>
    private static string NormalizeToJsonPath(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName) || propertyName == "$")
        {
            return "$";
        }

        // Accept an already-normalized value so repeated normalization is a no-op.
        string path = propertyName.StartsWith("$.", StringComparison.Ordinal)
            ? propertyName[2..]
            : propertyName;

        if (path.Length == 0)
        {
            return "$";
        }

        return $"$.{string.Join('.', path.Split('.').Select(CamelCaseSegment))}";
    }

    /// <summary>
    /// Camel-cases a single JSON-path segment's identifier with JsonNamingPolicy.CamelCase (so leading
    /// acronyms such as "APIKey" become "apiKey" rather than "aPIKey") while preserving any trailing
    /// array-index suffix (e.g. "ResourceClaims[0]" -> "resourceClaims[0]").
    /// </summary>
    private static string CamelCaseSegment(string segment)
    {
        int bracketIndex = segment.IndexOf('[', StringComparison.Ordinal);
        string identifier = bracketIndex >= 0 ? segment[..bracketIndex] : segment;
        string suffix = bracketIndex >= 0 ? segment[bracketIndex..] : string.Empty;

        if (identifier.Length == 0)
        {
            return segment;
        }

        return $"{JsonNamingPolicy.CamelCase.ConvertName(identifier)}{suffix}";
    }
}
