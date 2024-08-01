// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware
{
    internal class ParseBodyMiddleware(ILogger _logger) : IPipelineStep
    {
        private static readonly JsonSerializerOptions _serializerOptions =
            new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public static string GenerateFrontendErrorResponse(string errorDetail)
        {
            string[] errors = { errorDetail };

            var response = ForBadRequest(
                "The request could not be processed. See 'errors' for details.",
                [],
                errors
            );

            return JsonSerializer.Serialize(response, _serializerOptions);
        }

        public static string GenerateFrontendValidationErrorResponse(string errorDetail)
        {
            var validationErrors = new Dictionary<string, string[]>();

            var value = new List<string> { errorDetail };
            validationErrors.Add("$.", value.ToArray());

            var response = ForDataValidation(
                "Data validation failed. See 'validationErrors' for details.",
                validationErrors,
                []
            );

            return JsonSerializer.Serialize(response, _serializerOptions);
        }

        public async Task Execute(PipelineContext context, Func<Task> next)
        {
            _logger.LogDebug("Entering ParseBodyMiddleware - {TraceId}", context.FrontendRequest.TraceId);
            string errorMessage = "A non-empty request body is required.";

            try
            {
                if (string.IsNullOrWhiteSpace(context.FrontendRequest.Body))
                {
                    ErrorResponse(errorMessage);
                    return;
                }

                JsonNode? body = JsonNode.Parse(context.FrontendRequest.Body);

                Trace.Assert(body != null, "Unable to parse JSON");

                if (string.IsNullOrWhiteSpace(body.ToString()))
                {
                    ErrorResponse(errorMessage);
                    return;
                }
                context.ParsedBody = body;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Unable to parse the request body as JSON - {TraceId}",
                    context.FrontendRequest.TraceId
                );

                context.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    GenerateFrontendValidationErrorResponse(ex.Message),
                    Headers: []
                );

                return;
            }

            void ErrorResponse(string message)
            {
                context.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    GenerateFrontendErrorResponse(message),
                    Headers: []
                );
            }

            await next();
        }
    }
}
