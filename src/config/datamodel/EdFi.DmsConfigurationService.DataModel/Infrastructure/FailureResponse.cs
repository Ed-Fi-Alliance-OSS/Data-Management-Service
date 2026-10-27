// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.DataModel.Infrastructure;

public static class FailureResponse
{
    private const string TooManyRequestsDetail =
        "The number of allowed requests has been exceeded. Retry the request later.";

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string _typePrefix = "urn:ed-fi:management-api";
    private static readonly string _unauthorizedType = $"{_typePrefix}:security:authentication";
    private static readonly string _tooManyRequestsType = $"{_typePrefix}:too-many-requests";
    private static readonly string _forbiddenType = $"{_typePrefix}:security:authorization";
    private static readonly string _badRequestTypePrefix = $"{_typePrefix}:bad-request";
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

    /// <summary>
    /// The <c>error</c> of the CMS <c>/connect/token</c> 429. The DMS <c>/oauth/token</c> proxy's 429
    /// parser (<c>OAuthManager</c> in <c>src/dms</c>) believes a token-limit answer only with this code
    /// and <see cref="TooManyTokensMessage"/>'s sentence, and DMS's <c>OAuthManagerTests</c> builds its
    /// envelope from both, so changing either here fails the DMS unit suite.
    /// </summary>
    public const string TooManyTokensError = "temporarily_unavailable";

    /// <summary>
    /// The canonical token-limit sentence, formatted with the culture-invariant limit. This is the one
    /// copy of the cross-service text: the CMS <c>/connect/token</c> endpoint returns it as the
    /// <c>error_description</c> of its 429, and the DMS <c>/oauth/token</c> proxy's 429 parser
    /// (<c>OAuthManager</c> in <c>src/dms</c>) reads the limit back out of it and rebuilds its own
    /// message from the number. Rewording it means rewording that parser in the same change; DMS's
    /// <c>OAuthManagerTests</c> feeds this method's output through the parser, so a reword here
    /// fails the DMS unit suite.
    /// </summary>
    public static string TooManyTokensMessage(int limit) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Too many access tokens have been requested (limit is {limit}). Access tokens should be reused until they expire."
        );

    /// <summary>
    /// 429 response for a request refused by the <c>/connect/*</c> rate limiter (DMS-1482). The type
    /// mirrors DMS's established <c>urn:ed-fi:api:too-many-requests</c>, and the detail is the text DMS
    /// uses for its own rate-limit rejection. Distinct from <see cref="TooManyTokensMessage"/>, which is the
    /// active-token cap and whose text DMS parses.
    /// </summary>
    public static JsonNode ForTooManyRequests(string correlationId) =>
        CreateBaseJsonObject(
            detail: TooManyRequestsDetail,
            type: _tooManyRequestsType,
            title: "Too Many Requests",
            status: 429,
            correlationId: correlationId,
            errors: []
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

    public static JsonNode ForBadRequest(string detail, string correlationId, string[]? errors = null) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _badRequestTypePrefix,
            title: "Bad Request",
            status: 400,
            correlationId: correlationId,
            errors: errors
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

    public static JsonNode ForMethodNotAllowed(string correlationId) =>
        CreateBaseJsonObject(
            detail: "The request construction was invalid.",
            type: _methodNotAllowedType,
            title: "Method Not Allowed",
            status: 405,
            correlationId: correlationId,
            []
        );

    public static JsonNode ForUnsupportedMediaType(string correlationId) =>
        CreateBaseJsonObject(
            detail: "The value specified in the 'Content-Type' header is not supported by this host.",
            type: _unsupportedMediaTypeType,
            title: "Unsupported Media Type",
            status: 415,
            correlationId: correlationId,
            []
        );

    /// <summary>
    /// RFC 9457 fallback for a reachable HTTP status that has no ticket-mandated, Knowledge-Base, or
    /// established Ed-Fi/DMS platform taxonomy URI. Uses <c>about:blank</c> with the standard HTTP
    /// reason phrase as the title; no URI is invented.
    /// </summary>
    public static JsonNode ForUnclassifiedStatus(int status, string reasonPhrase, string correlationId) =>
        CreateBaseJsonObject(
            detail: "",
            type: "about:blank",
            title: reasonPhrase,
            status: status,
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

    public static JsonNode ForDependentItemExists(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: $"{_conflictTypePrefix}:dependent-item-exists",
            title: "Dependent Item Exists",
            status: 409,
            correlationId: correlationId,
            []
        );

    public static JsonNode ForEducationOrganizationScopeConflict(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: $"{_conflictTypePrefix}:education-organization-scope",
            title: "Education Organization Scope Conflict",
            status: 409,
            correlationId: correlationId,
            []
        );

    public static JsonNode ForUnresolvedReference(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: $"{_conflictTypePrefix}:unresolved-reference",
            title: "Unresolved Reference",
            status: 409,
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
                .GroupBy(x => x.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage).ToArray())
        );

    /// <summary>
    /// Structured 400 <c>urn:ed-fi:management-api:bad-request:parameter</c> response for a query/pagination
    /// parameter failure (as opposed to a request-body data-validation failure). Per the Knowledge
    /// Base's "Invalid Limit Count" example, <c>validationErrors</c> stays empty and the specific,
    /// pre-sanitized reasons are carried in <c>errors</c>.
    /// </summary>
    public static JsonNode ForParameterValidation(string[] errors, string correlationId) =>
        CreateBaseJsonObject(
            detail: "Parameter validation failed. See 'errors' for details.",
            type: $"{_badRequestTypePrefix}:parameter",
            title: "Parameter Validation Failed",
            status: 400,
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

    /// <summary>
    /// 502 <c>urn:ed-fi:management-api:bad-gateway:partial-sync</c>: the Application change is saved,
    /// but the synchronization of the API clients in <paramref name="apiClientIds"/> with the identity
    /// provider is not confirmed, so they remain pending until a later request completes it. A listed
    /// client's provider update may have failed, or may have succeeded with its completion unrecorded.
    /// </summary>
    public static JsonNode ForPartialSync(int[] apiClientIds, string correlationId)
    {
        JsonObject response = CreateBaseJsonObject(
            detail: apiClientIds.Length == 1
                ? "The Application change is saved, but 1 API client is still pending synchronization with the identity provider. Send the same request again to retry it."
                : $"The Application change is saved, but {apiClientIds.Length} API clients are still pending synchronization with the identity provider. Send the same request again to retry them.",
            type: $"{_badGatewayTypePrefix}:partial-sync",
            title: "Bad Gateway",
            status: 502,
            correlationId: correlationId,
            errors: []
        );
        response["apiClientIds"] = JsonSerializer.SerializeToNode(apiClientIds, _serializerOptions);
        return response;
    }

    public static JsonNode ForUnknown(string correlationId) =>
        CreateBaseJsonObject(
            detail: "",
            type: _unavailableType,
            title: "Internal Server Error",
            status: 500,
            correlationId: correlationId,
            errors: []
        );
}
