// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Logs requests and responses, and converts exceptions to 500s
/// </summary>
internal class CoreLoggingMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("FrontendRequest: {FrontendRequest}", context.FrontendRequest);

        try
        {
            await next();
            _logger.LogDebug("FrontendResponse: {FrontendResponse}", context.FrontendResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown Error - {TraceId}", context.FrontendRequest.TraceId);

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

            // Replace the frontend response (if any) with a 500 error
            context.FrontendResponse = new(
                StatusCode: 400,
                JsonSerializer.Serialize(failureResponse, options),
                Headers: []
            );
        }
    }
}
