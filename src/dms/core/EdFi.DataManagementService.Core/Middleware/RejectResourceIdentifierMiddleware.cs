// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware
{
    internal class RejectResourceIdentifierMiddleware(ILogger _logger) : IPipelineStep
    {
        public static JsonNode GenerateFrontendErrorResponse(string errorDetail, TraceId traceId)
        {
            var errors = new List<string> { errorDetail };

            return ForDataValidation(
                "The request data was constructed incorrectly.",
                traceId,
                [],
                [.. errors]
            );
        }

        public async Task Execute(PipelineContext context, Func<Task> next)
        {
            _logger.LogDebug(
                "Entering RejectResourceIdentifierMiddleware - {TraceId}",
                context.FrontendRequest.TraceId
            );
            if (context.FrontendRequest.Body != null)
            {
                JsonNode? body = JsonNode.Parse(context.FrontendRequest.Body);

                if (body != null && PropertyExists(body, "id"))
                {
                    context.FrontendResponse = new FrontendResponse(
                        StatusCode: 400,
                        GenerateFrontendErrorResponse(
                            "Resource identifiers cannot be assigned by the client. The 'id' property should not be included in the request body.",
                            context.FrontendRequest.TraceId
                        ),
                        Headers: []
                    );
                    return;
                }
            }

            await next();

            bool PropertyExists(JsonNode? jsonNode, string propertyName)
            {
                if (jsonNode is JsonObject jsonObject)
                {
                    var properties = jsonObject.Where(x =>
                        x.Key.Equals(propertyName, StringComparison.InvariantCultureIgnoreCase)
                    );
                    if (properties.Any())
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
