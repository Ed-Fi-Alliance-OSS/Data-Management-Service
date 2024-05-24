// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
                    _logger.LogError(ex, "Unknown Error - {TraceId}", context.FrontendRequest.TraceId);

                    FailureResponse.GenerateFrontendErrorResponse(context, ex.Message, 400);
                }
            }

            await next();
        }
    }
}
