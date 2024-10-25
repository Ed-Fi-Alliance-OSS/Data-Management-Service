// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
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
            var response = context.Response;
            response.ContentType = "application/json";
            logger.LogError(ex.Message + " - TraceId: {TraceId}", context.TraceIdentifier);

            switch (ex)
            {
                case ValidationException validationException:
                    var validationResponse = new
                    {
                        title = "Validation failed",
                        errors = new Dictionary<string, List<string>>(),
                    };

                    foreach (var error in validationException.Errors)
                    {
                        if (!validationResponse.errors.ContainsKey(error.PropertyName))
                        {
                            validationResponse.errors[error.PropertyName] = new List<string>();
                        }
                        validationResponse
                            .errors[error.PropertyName]
                            .Add(error.ErrorMessage.Replace("\u0027", "'"));
                    }
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await response.WriteAsync(JsonSerializer.Serialize(validationResponse));
                    break;

                // Bad credentials
                case IdentityException:
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    await response.WriteAsync(
                        JsonSerializer.Serialize(
                            new
                            {
                                title = "Client token generation failed",
                                message = "Invalid client or Invalid client credentials."
                            }
                        )
                    );
                    break;
                // Keycloak is unreachable
                case AggregateException
                    when (
                        ex.Message.Contains(
                            "No connection could be made because the target machine actively refused it"
                        )
                    ):
                    response.StatusCode = (int)HttpStatusCode.BadGateway;
                    await response.WriteAsync(
                        JsonSerializer.Serialize(new { message = "Keycloak is unreachable." })
                    );
                    break;
                // Invalid realm
                case AggregateException when (ex.Message.Contains("Call failed with status code 404")):
                    response.StatusCode = (int)HttpStatusCode.BadGateway;
                    await response.WriteAsync(
                        JsonSerializer.Serialize(new { message = "Invalid real, please check the configuration." })
                    );
                    break;

                default:
                    logger.LogError(
                        JsonSerializer.Serialize(
                            new
                            {
                                message = "An uncaught error has occurred",
                                error = new { ex.Message, ex.StackTrace },
                                traceId = context.TraceIdentifier,
                            }
                        )
                    );
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await response.WriteAsync(
                        JsonSerializer.Serialize(
                            new
                            {
                                message = "The server encountered an unexpected condition that prevented it from fulfilling the request.",
                            }
                        )
                    );
                    break;
            }
        }
    }
}
