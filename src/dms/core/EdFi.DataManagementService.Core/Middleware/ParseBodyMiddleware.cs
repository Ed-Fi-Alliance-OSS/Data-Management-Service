// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware
{
    internal class ParseBodyMiddleware(ILogger _logger) : IPipelineStep
    {
        private static JsonNode GenerateFrontendErrorResponse(string errorDetail, TraceId traceId)
        {
            string[] errors = [errorDetail];

            var response = ForBadRequest(
                "The request could not be processed. See 'errors' for details.",
                traceId,
                [],
                errors
            );

            return response;
        }

        private static JsonNode GenerateFrontendValidationErrorResponse(string errorDetail, TraceId traceId)
        {
            Dictionary<string, string[]> validationErrors = new();

            List<string> value = [errorDetail];
            validationErrors.Add("$.", value.ToArray());

            var response = ForDataValidation(
                "Data validation failed. See 'validationErrors' for details.",
                traceId,
                validationErrors,
                []
            );

            return response;
        }

        public async Task Execute(RequestData requestData, Func<Task> next)
        {
            _logger.LogDebug(
                "Entering ParseBodyMiddleware - {TraceId}",
                requestData.FrontendRequest.TraceId.Value
            );

            try
            {
                if (string.IsNullOrWhiteSpace(requestData.FrontendRequest.Body))
                {
                    requestData.FrontendResponse = new FrontendResponse(
                        StatusCode: 400,
                        GenerateFrontendErrorResponse(
                            "A non-empty request body is required.",
                            requestData.FrontendRequest.TraceId
                        ),
                        Headers: []
                    );
                    return;
                }

                JsonNode? body = JsonNode.Parse(requestData.FrontendRequest.Body);

                Trace.Assert(body != null, "Unable to parse JSON");

                requestData.ParsedBody = body;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Unable to parse the request body as JSON - {TraceId}",
                    requestData.FrontendRequest.TraceId.Value
                );

                requestData.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    GenerateFrontendValidationErrorResponse(ex.Message, requestData.FrontendRequest.TraceId),
                    Headers: []
                );

                return;
            }

            await next();
        }
    }
}
