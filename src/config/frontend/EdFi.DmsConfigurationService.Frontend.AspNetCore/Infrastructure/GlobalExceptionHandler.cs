// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend;
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
        var traceId = httpContext.TraceIdentifier;
        logger.LogError(
            JsonSerializer.Serialize(
                new
                {
                    message = "An uncaught error has occurred",
                    error = new { exception.Message, exception.StackTrace },
                    traceId = traceId,
                }
            )
        );
        var response = httpContext.Response;
        response.ContentType = "application/problem+json";
        response.Headers["TraceId"] = traceId;

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
                        FailureResponse.ForBadRequest(badHttpRequest.Message, traceId),
                        relaxedSerializer
                    ),
                    cancellationToken: cancellationToken
                );
                break;
            case FluentValidation.ValidationException validationException:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await response.WriteAsync(
                    JsonSerializer.Serialize(
                        FailureResponse.ForDataValidation(validationException.Errors, traceId),
                        relaxedSerializer
                    ),
                    cancellationToken: cancellationToken
                );
                break;
            case IdentityException identityException:
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                string responseString = JsonSerializer.Serialize(
                    FailureResponse.ForUnauthorized(identityException.Message, traceId)
                );
                await response.WriteAsync(
                    JsonSerializer.Serialize(
                        FailureResponse.ForUnauthorized(identityException.Message, traceId),
                        relaxedSerializer
                    ),
                    cancellationToken: cancellationToken
                );
                break;
            case IdentityProviderException keycloakException:
                if (keycloakException.IdentityProviderError is IdentityProviderError.Unreachable)
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
                        FailureResponse.ForBadGateway(
                            keycloakException.IdentityProviderError.FailureMessage,
                            traceId
                        ),
                        relaxedSerializer
                    ),
                    cancellationToken: cancellationToken
                );
                break;
            default:
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await response.WriteAsync(
                    JsonSerializer.Serialize(FailureResponse.ForUnhandled(traceId), relaxedSerializer),
                    cancellationToken: cancellationToken
                );
                break;
        }
        return true;
    }
}
