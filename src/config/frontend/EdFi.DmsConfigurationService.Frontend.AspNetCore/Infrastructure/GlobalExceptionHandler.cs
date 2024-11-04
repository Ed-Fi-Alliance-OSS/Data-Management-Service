// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        logger.LogError(
            JsonSerializer.Serialize(
                new
                {
                    message = "An uncaught error has occurred",
                    error = new { exception.Message, exception.StackTrace },
                    traceId = httpContext.TraceIdentifier,
                }
            )
        );
        var response = httpContext.Response;
        response.ContentType = "application/problem+json";
        response.Headers["TraceId"] = httpContext.TraceIdentifier;

        var relaxedSerializer = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
        };

        switch (exception)
        {
            case BadHttpRequestException badHttpRequest:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await response.WriteAsync(
                    JsonSerializer.Serialize(
                        FailureResponse.ForBadRequest(badHttpRequest.Message),
                        relaxedSerializer
                    ),
                    cancellationToken: cancellationToken
                );
                break;
            case FluentValidation.ValidationException validationException:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await response.WriteAsync(
                    JsonSerializer.Serialize(
                        FailureResponse.ForDataValidation(validationException.Errors),
                        relaxedSerializer
                    ),
                    cancellationToken: cancellationToken
                );
                break;
            case IdentityException identityException:
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                string responseString = JsonSerializer.Serialize(
                    FailureResponse.ForUnauthorized(identityException.Message)
                );
                await response.WriteAsync(
                    JsonSerializer.Serialize(
                        FailureResponse.ForUnauthorized(identityException.Message),
                        relaxedSerializer
                    ),
                    cancellationToken: cancellationToken
                );
                break;
            case KeycloakException keycloakException:
                if (keycloakException.KeycloakError is KeycloakError.Unreachable)
                {
                    logger.LogCritical(
                        JsonSerializer.Serialize(
                            new
                            {
                                message = "Keycloak is unreachable",
                                error = new { exception.Message, exception.StackTrace },
                                traceId = httpContext.TraceIdentifier,
                            }
                        )
                    );
                }
                response.StatusCode = (int)HttpStatusCode.BadGateway;
                await response.WriteAsync(
                    JsonSerializer.Serialize(
                        FailureResponse.ForBadGateway(keycloakException.KeycloakError.FailureMessage),
                        relaxedSerializer
                    ),
                    cancellationToken: cancellationToken
                );
                break;
            default:
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await response.WriteAsync(
                    JsonSerializer.Serialize(FailureResponse.ForUnhandled(), relaxedSerializer),
                    cancellationToken: cancellationToken
                );
                break;
        }
        return true;
    }
}

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
        Dictionary<string, string[]>? validationErrors = null
    )
    {
        return new JsonObject
        {
            ["detail"] = detail,
            ["type"] = type,
            ["title"] = title,
            ["status"] = status,
            ["validationErrors"] =
                validationErrors != null
                    ? JsonSerializer.SerializeToNode(validationErrors, _serializerOptions)
                    : new JsonObject(),
        };
    }

    public static JsonNode ForBadRequest(string detail) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _badRequestTypePrefix,
            title: "Bad Request",
            status: 400,
            []
        );

    public static JsonNode ForDataValidation(IEnumerable<ValidationFailure> validationFailures) =>
        CreateBaseJsonObject(
            detail: "",
            type: $"{_badRequestTypePrefix}:data-validation-failed",
            title: "Data Validation Failed",
            status: 400,
            validationFailures
                .GroupBy(x => x.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage).ToArray())
        );

    public static JsonNode ForUnauthorized(string detail) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _unauthorizedType,
            title: "Unauthorized",
            status: 401,
            validationErrors: []
        );

    public static JsonNode ForBadGateway(string detail) =>
        CreateBaseJsonObject(
            detail: detail,
            type: _badGatewayTypePrefix,
            title: "Bad Gateway",
            status: 502,
            validationErrors: []
        );

    public static JsonNode ForUnhandled() =>
        CreateBaseJsonObject(
            detail: "",
            type: _unavailableType,
            title: "Service Unavailable",
            status: 401,
            validationErrors: []
        );
}
