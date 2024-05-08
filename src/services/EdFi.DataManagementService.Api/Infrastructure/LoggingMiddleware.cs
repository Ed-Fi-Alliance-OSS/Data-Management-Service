// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using EdFi.DataManagementService.Api.Core.Response;

namespace EdFi.DataManagementService.Api.Infrastructure;

public class LoggingMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context, ILogger<LoggingMiddleware> logger)
    {
        try
        {
            logger.LogInformation(
                JsonSerializer.Serialize(
                    new
                    {
                        method = context.Request.Method,
                        path = context.Request.Path.Value,
                        traceId = context.TraceIdentifier,
                        clientId = context.Request.Host
                    }
                )
            );

            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(
                JsonSerializer.Serialize(
                    new
                    {
                        message = "An uncaught error has occurred",
                        error = new { ex.Message, ex.StackTrace },
                        traceId = context.TraceIdentifier
                    }
                )
            );

            FailureResponse failureResponse;

            var validationErrors = new Dictionary<string, string[]>();

            var value = new List<string>
            {
                ex.Message
            };
            validationErrors.Add("$.", value.ToArray());

            failureResponse = FailureResponse.ForDataValidation(
                "Data validation failed. See 'validationErrors' for details.",
                validationErrors,
                new List<string>().ToArray()
            );

            var options = new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var response = context.Response;
            if (!response.HasStarted)
            {
                response.ContentType = "application/json";
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await response.WriteAsync(JsonSerializer.Serialize(failureResponse, options));
            }
        }
    }
}
