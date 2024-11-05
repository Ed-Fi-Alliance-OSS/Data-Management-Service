// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

public class RequestLoggingMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task Invoke(HttpContext context, ILogger<RequestLoggingMiddleware> logger)
    {
        try
        {
            if (context.Request.Path.StartsWithSegments(new PathString("/.well-known")))
            {
                // Requests to the OpenId Connect ".well-known" endpoint are too chatty for informational logging, but could be useful in debug logging.
                logger.LogDebug(
                    JsonSerializer.Serialize(
                        new { path = context.Request.Path.Value, traceId = context.TraceIdentifier }
                    )
                );
            }
            else
            {
                logger.LogInformation(
                    JsonSerializer.Serialize(
                        new { path = context.Request.Path.Value, traceId = context.TraceIdentifier }
                    )
                );
            }
            await _next(context);
        }
        catch (Exception ex)
        {
            int statusCode = ex switch
            {
                ValidationException => (int)HttpStatusCode.BadRequest,
                BadHttpRequestException => (int)HttpStatusCode.BadRequest,
                IdentityException => (int)HttpStatusCode.Unauthorized,
                KeycloakException ke => ke.KeycloakError switch
                {
                    KeycloakError.Unreachable => (int)HttpStatusCode.BadGateway,
                    KeycloakError.Unauthorized => (int)HttpStatusCode.Unauthorized,
                    KeycloakError.NotFound => (int)HttpStatusCode.NotFound,
                    KeycloakError.Forbidden => (int)HttpStatusCode.Forbidden,
                    _ => (int)HttpStatusCode.InternalServerError,
                },
                _ => (int)HttpStatusCode.InternalServerError,
            };

            JsonNode? responseBody = ex switch
            {
                ValidationException validationEx => JsonNode.Parse(
                    JsonSerializer.Serialize(
                        new
                        {
                            title = "Validation failed.",
                            errors = validationEx
                                .Errors.GroupBy(e => e.PropertyName)
                                .ToDictionary(
                                    g => g.Key,
                                    g => g.Select(e => e.ErrorMessage.Replace("\u0027", "'")).ToList()
                                ),
                        }
                    )
                ),
                IdentityException => JsonNode.Parse(
                    JsonSerializer.Serialize(
                        new { title = "Client token generation failed.", message = ex.Message }
                    )
                ),
                KeycloakException ke => ke.KeycloakError switch
                {
                    KeycloakError.Unreachable => JsonNode.Parse(
                        JsonSerializer.Serialize(
                            new { title = "Keycloak is unreachable.", message = ex.Message }
                        )
                    ),
                    KeycloakError.NotFound => JsonNode.Parse(
                        JsonSerializer.Serialize(
                            new { title = "Invalid realm.", message = "Please check the configuration." }
                        )
                    ),
                    KeycloakError.Unauthorized => JsonNode.Parse(
                        JsonSerializer.Serialize(new { title = "Bad Credentials.", message = ex.Message })
                    ),
                    KeycloakError.Forbidden => JsonNode.Parse(
                        JsonSerializer.Serialize(
                            new { title = "Insufficient Permissions.", message = ex.Message }
                        )
                    ),
                    _ => JsonNode.Parse(
                        JsonSerializer.Serialize(
                            new { title = "Unexpected Keycloak Error.", message = ex.Message }
                        )
                    ),
                },
                BadHttpRequestException => JsonNode.Parse(
                    JsonSerializer.Serialize(new { title = "Invalid Request Format.", message = ex.Message })
                ),
                _ => JsonNode.Parse(
                    JsonSerializer.Serialize(
                        new
                        {
                            title = "Unexpected Server Error.",
                            message = "The server encountered an unexpected condition that prevented it from fulfilling the request.",
                        }
                    )
                ),
            };

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.Headers["TraceId"] = context.TraceIdentifier;

            await context.Response.WriteAsync(responseBody?.ToJsonString() ?? string.Empty);

            if (ex is KeycloakException { KeycloakError: KeycloakError.Unreachable })
            {
                logger.LogCritical(ex.Message + " - TraceId: {TraceId}", context.TraceIdentifier);
            }
            else
            {
                logger.LogError(ex.Message + " - TraceId: {TraceId}", context.TraceIdentifier);
            }
        }
    }
}
