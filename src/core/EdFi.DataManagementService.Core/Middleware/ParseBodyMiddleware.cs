// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware
{
    internal class ParseBodyMiddleware(ILogger _logger) : IPipelineStep
    {
        public async Task Execute(PipelineContext context, Func<Task> next)
        {
            _logger.LogDebug("Entering ParseBodyMiddleware - {TraceId}", context.FrontendRequest.TraceId);

            if (context.FrontendRequest.Body != null)
            {
                try
                {
                    var body = JsonNode.Parse(context.FrontendRequest.Body);

                    if (body != null)
                    {
                        context.ParsedBody = body;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unable to parse the request body as JSON - {TraceId}", context.FrontendRequest.TraceId);

                    context.FrontendResponse = new(
                        StatusCode: 400,
                        FailureResponse.GenerateFrontendErrorResponse(ex.Message),
                        Headers: []
                    );
                    return;
                }
            }

            await next();
        }
    }
}
