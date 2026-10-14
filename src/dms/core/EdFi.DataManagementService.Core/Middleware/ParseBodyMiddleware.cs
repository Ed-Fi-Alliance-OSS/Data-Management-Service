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

        private static void SetValidationErrorResponse(RequestInfo requestInfo, string errorDetail)
        {
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                GenerateFrontendValidationErrorResponse(errorDetail, requestInfo.FrontendRequest.TraceId),
                Headers: []
            );
        }

        public async Task Execute(RequestInfo requestInfo, Func<Task> next)
        {
            _logger.LogDebug(
                "Entering ParseBodyMiddleware - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            if (requestInfo.FrontendRequest.BodyParseErrorMessage != null)
            {
                _logger.LogDebug(
                    "Unable to parse the request body as JSON - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );

                SetValidationErrorResponse(requestInfo, requestInfo.FrontendRequest.BodyParseErrorMessage);
                return;
            }

            if (requestInfo.FrontendRequest.ParsedBody != null)
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
                        Headers: []
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

                    Trace.Assert(body != null, "Unable to parse JSON", "");

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

            // The parser accepts unpaired-surrogate escapes such as \uD800 and defers the failure to
            // the first read of that string, which would otherwise surface later as a 5xx. The
            // duplicate-property scan reads every property name (the frontend's, or the one below for a
            // body parsed here), and the walk reads every string value, so either failure becomes the
            // malformed-body 400. A body with a
            // duplicate property is left to DuplicatePropertiesMiddleware: enumerating its object would
            // throw, and that step already answers it with a path-attributed 400.
            try
            {
                string? duplicatePropertyPath = requestInfo.FrontendRequest.DuplicatePropertyPath;
                if (
                    duplicatePropertyPath == null
                    && requestInfo.FrontendRequest.ParsedBody == null
                    && requestInfo.FrontendRequest.Body != null
                )
                {
                    duplicatePropertyPath = DuplicatePropertiesMiddleware.FindDuplicatePropertyPath(
                        requestInfo.FrontendRequest.Body
                    );
                }

                if (duplicatePropertyPath == null)
                {
                    MaterializeStrings(requestInfo.ParsedBody);
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(
                    ex,
                    "The request body contains a malformed string - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );

                SetValidationErrorResponse(requestInfo, ex.Message);

                return;
            }

            await next();
        }

        /// <summary>
        /// Reads every string value, so a malformed one throws here with its path. Property names were
        /// already read by the duplicate-property scan, which rejects a malformed one first.
        /// </summary>
        private static void MaterializeStrings(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject jsonObject:
                    foreach (var property in jsonObject)
                    {
                        MaterializeStrings(property.Value);
                    }
                    break;

                case JsonArray jsonArray:
                    foreach (var item in jsonArray)
                    {
                        MaterializeStrings(item);
                    }
                    break;

                case JsonValue jsonValue
                    when jsonValue.GetValueKind() == System.Text.Json.JsonValueKind.String:
                    try
                    {
                        _ = jsonValue.GetValue<string>();
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new InvalidOperationException($"{ex.Message} Path: {jsonValue.GetPath()}", ex);
                    }
                    break;
            }
        }
    }
}
