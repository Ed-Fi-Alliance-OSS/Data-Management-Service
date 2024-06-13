// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class ValidateQueryMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ValidateQueryMiddleware - {TraceId}", context.FrontendRequest.TraceId);

        int offset = 0;
        int limit = 25;
        bool totalCount = false;
        List<string> errors = new();

        if (context.FrontendRequest.QueryParameters.ContainsKey("offset"))
        {
            if (!int.TryParse(context.FrontendRequest.QueryParameters["offset"], out int offserVal) ||
                offserVal < 0)
            {
                errors.Add("Offset must be a numeric value greater than or equal to 0.");
            }
            else
            {
                offset = int.TryParse(context.FrontendRequest.QueryParameters["offset"], out int offsetResult)
                    ? offsetResult
                    : offset;
            }
        }

        if (context.FrontendRequest.QueryParameters.ContainsKey("limit"))
        {
            if (!int.TryParse(context.FrontendRequest.QueryParameters["limit"], out int limitVal) ||
                limitVal < 0)
            {
                errors.Add("Limit must be a numeric value greater than or equal to 0.");
            }
            else
            {
                limit = int.TryParse(context.FrontendRequest.QueryParameters["limit"], out int limitResult)
                    ? limitResult
                    : limit;
            }
        }

        if (context.FrontendRequest.QueryParameters.ContainsKey("totalCount"))
        {
            if (!bool.TryParse(context.FrontendRequest.QueryParameters["totalCount"], out totalCount))
            {
                errors.Add("TotalCount must be a boolean value.");
            }
            else
            {
                totalCount =
                    bool.TryParse(context.FrontendRequest.QueryParameters["totalCount"], out bool totalValue)
                        ? totalValue
                        : totalCount;
            }
        }

        if (errors.Count > 0)
        {
            FailureResponse failureResponse = FailureResponse.ForBadRequest(
                "The request could not be processed. See 'errors' for details.",
                null,
                errors.ToArray()
            );

            _logger.LogDebug(
                "'{Status}'.'{EndpointName}' - {TraceId}",
                failureResponse.status.ToString(),
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId
            );

            var options = new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            };

            context.FrontendResponse = new FrontendResponse(
                failureResponse.status,
                JsonSerializer.Serialize(failureResponse, options),
                []
            );
            return;
        }

        context.PaginationParameters = new PaginationParameters(limit, offset, totalCount);

        await next();
    }
}
