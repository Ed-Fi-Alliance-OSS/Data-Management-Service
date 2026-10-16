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
    /// <summary>
    /// Parses the request body as JSON, answering a malformed or empty body with 400.
    /// </summary>
    /// <param name="_logger">The logger.</param>
    /// <param name="_errorContentType">
    /// The media type of those 400 responses. The identity JSON-body pipeline passes
    /// application/problem+json, which its published OpenAPI document declares; every other
    /// pipeline keeps the application/json default.
    /// </param>
    internal class ParseBodyMiddleware(ILogger _logger, string _errorContentType = "application/json")
        : IPipelineStep
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

        private void SetValidationErrorResponse(RequestInfo requestInfo, string errorDetail)
        {
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                GenerateFrontendValidationErrorResponse(errorDetail, requestInfo.FrontendRequest.TraceId),
                Headers: [],
                ContentType: _errorContentType
            );
        }

        public async Task Execute(RequestInfo requestInfo, Func<Task> next)
        {
            _logger.LogDebug(
                "Entering ParseBodyMiddleware - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            if (requestInfo.FrontendRequest.BodyParseErrorMessage is not null)
            {
                _logger.LogDebug(
                    "Unable to parse the request body as JSON - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );

                SetValidationErrorResponse(requestInfo, requestInfo.FrontendRequest.BodyParseErrorMessage);
                return;
            }

            if (requestInfo.FrontendRequest.ParsedBody is not null)
            {
                requestInfo.ParsedBody = requestInfo.FrontendRequest.ParsedBody;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(requestInfo.FrontendRequest.Body))
                {
                    requestInfo.FrontendResponse = new FrontendResponse(
                        StatusCode: 400,
                        GenerateFrontendErrorResponse(
                            "A non-empty request body is required.",
                            requestInfo.FrontendRequest.TraceId
                        ),
                        Headers: [],
                        ContentType: _errorContentType
                    );
                    return;
                }

                // Only the parse itself is guarded, and only for the one exception that means the
                // body was malformed. A failure raised by a later step, or by anything other than
                // the JSON grammar, is not a client validation error and its message is internal,
                // so it must travel on to the pipeline's exception handler instead of being
                // answered as a 400 here.
                try
                {
                    JsonNode? body = JsonNode.Parse(requestInfo.FrontendRequest.Body);

                    Trace.Assert(body is not null, "Unable to parse JSON", "");

                    requestInfo.ParsedBody = body;
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Unable to parse the request body as JSON - {TraceId}",
                        requestInfo.FrontendRequest.TraceId.Value
                    );

                    SetValidationErrorResponse(requestInfo, ex.Message);

                    return;
                }
            }

            await next();
        }
    }
}
