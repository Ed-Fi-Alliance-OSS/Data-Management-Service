// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

internal static class FailureResponse
{
    private static readonly JsonSerializerOptions _serializerOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string _typePrefix = "urn:ed-fi:api";
    private static readonly string _badRequestTypePrefix = $"{_typePrefix}:bad-request";
    private static readonly string _badGatewayTypePrefix = $"{_typePrefix}:bad-gateway";
    private static readonly string _unauthorizedType = $"{_typePrefix}:unauthorized";
    private static readonly string _unavailableType = $"{_typePrefix}:service-unavailable";

    private static JsonObject CreateBaseJsonObject(
        string detail,
        string type,
        string title,
        int status,
        string correlationId,
        Dictionary<string, string[]>? validationErrors = null
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
        };
    }

    public static JsonNode ForBadRequest(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _badRequestTypePrefix,
            title: "Bad Request",
            status: 400,
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

    public static JsonNode ForUnauthorized(string detail, string correlationId) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _unauthorizedType,
            title: "Unauthorized",
            status: 401,
            correlationId: correlationId,
            validationErrors: []
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

    public static JsonNode ForUnhandled(string correlationId) =>
        CreateBaseJsonObject(
            detail: "",
            type: _unavailableType,
            title: "Service Unavailable",
            status: 401,
            correlationId: correlationId,
            validationErrors: []
        );
}
