// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Model;
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
            FrontendResponse frontendResponse =
                new()
                {
                    StatusCode = ex switch
                    {
                        ValidationException => (int)HttpStatusCode.BadRequest,
                        BadHttpRequestException => (int)HttpStatusCode.BadRequest,
                        IdentityException => (int)HttpStatusCode.Unauthorized,
                        KeycloakException ke when ke.Message.Contains("No connection could be made")
                            => (int)HttpStatusCode.BadGateway,
                        KeycloakException ke when ke.Message.Contains("status code 404")
                            => (int)HttpStatusCode.NotFound,
                        _ => (int)HttpStatusCode.InternalServerError,
                    },
                    Body = ex switch
                    {
                        ValidationException validationEx
                            => JsonNode.Parse(
                                JsonSerializer.Serialize(
                                    new
                                    {
                                        title = "Validation failed.",
                                        errors = validationEx
                                            .Errors.GroupBy(e => e.PropertyName)
                                            .ToDictionary(
                                                g => g.Key,
                                                g =>
                                                    g.Select(e => e.ErrorMessage.Replace("\u0027", "'"))
                                                        .ToList()
                                            )
                                    }
                                )
                            ),

                        IdentityException
                            => JsonNode.Parse(
                                JsonSerializer.Serialize(
                                    new { title = "Client token generation failed.", message = ex.Message }
                                )
                            ),

                        KeycloakException ke when ke.Message.Contains("No connection could be made")
                            => JsonNode.Parse(
                                JsonSerializer.Serialize(
                                    new { title = "Keycloak is unreachable.", message = ex.Message, }
                                )
                            ),

                        KeycloakException ke when ke.Message.Contains("status code 404")
                            => JsonNode.Parse(
                                JsonSerializer.Serialize(
                                    new
                                    {
                                        title = "Invalid realm.",
                                        message = "Please check the configuration."
                                    }
                                )
                            ),

                        BadHttpRequestException
                            => JsonNode.Parse(
                                JsonSerializer.Serialize(
                                    new { title = "Invalid Request Format.", message = ex.Message }
                                )
                            ),

                        _
                            => JsonNode.Parse(
                                JsonSerializer.Serialize(
                                    new
                                    {
                                        title = "Unexpected Server Error.",
                                        message = "The server encountered an unexpected condition that prevented it from fulfilling the request."
                                    }
                                )
                            )
                    },
                    Headers = new Dictionary<string, string> { { "TraceId", context.TraceIdentifier } }
                };

            if (ex is KeycloakException)
            {
                logger.LogCritical(ex.Message + " - TraceId: {TraceId}", context.TraceIdentifier);
            }
            else
            {
                logger.LogError(ex.Message + " - TraceId: {TraceId}", context.TraceIdentifier);
            }

            context.Response.StatusCode = frontendResponse.StatusCode;
            context.Response.ContentType = frontendResponse.ContentType;
            foreach (var header in frontendResponse.Headers)
            {
                context.Response.Headers[header.Key] = header.Value;
            }

            await context.Response.WriteAsync(frontendResponse.Body?.ToJsonString() ?? string.Empty);
        }
    }
}
