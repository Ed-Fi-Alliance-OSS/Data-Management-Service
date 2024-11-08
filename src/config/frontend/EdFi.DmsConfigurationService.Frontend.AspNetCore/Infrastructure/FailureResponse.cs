// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation.Results;
using Npgsql.Internal;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

internal static class FailureResponse
{
    private static readonly JsonSerializerOptions _serializerOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string _typePrefix = "urn:ed-fi:api";
    private static readonly string _unauthorizedType = $"{_typePrefix}:security:authentication";
    private static readonly string _forbiddenType = $"{_typePrefix}:security:authorization";
    private static readonly string _badRequestTypePrefix = $"{_typePrefix}:bad-request";
    private static readonly string _notFoundTypePrefix = $"{_typePrefix}:not-found";
    private static readonly string _badGatewayTypePrefix = $"{_typePrefix}:bad-gateway";
    private static readonly string _unavailableType = $"{_typePrefix}:internal-server-error";

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

    public static JsonNode ForUnauthorized(string title, string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _unauthorizedType,
            title: title,
            status: 401,
            correlationId: correlationId,
            validationErrors: [],
            errors: []
        );

    public static JsonNode ForForbidden(string title, string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _forbiddenType,
            title: title,
            status: 403,
            correlationId: correlationId,
            validationErrors: [],
            errors: []
        );

    public static JsonNode ForBadRequest(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _badRequestTypePrefix,
            title: "Bad Request",
            status: 400,
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

    public static JsonNode ForDataValidation(
        IEnumerable<ValidationFailure> validationFailures,
        string correlationId
    ) =>
        CreateBaseJsonObject(
            detail: "",
            type: $"{_badRequestTypePrefix}:data-validation-failed",
            title: "Data Validation Failed",
            status: 400,
            correlationId: correlationId,
            validationFailures
                .GroupBy(x => x.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage).ToArray())
        );

    public static JsonNode ForBadGateway(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _badGatewayTypePrefix,
            title: "Bad Gateway",
            status: 502,
            correlationId: correlationId,
            validationErrors: []
        );

    public static JsonNode ForUnknown(string correlationId) =>
        CreateBaseJsonObject(
            detail: "",
            type: _unavailableType,
            title: "Internal Server Error",
            status: 500,
            correlationId: correlationId,
            validationErrors: []
        );
}
