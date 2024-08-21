// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Middleware;

internal class ValidateQueryMiddleware(ILogger _logger) : IPipelineStep
{
    private static readonly string[] _disallowedQueryFields = ["limit", "offset", "totalCount"];

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ValidateQueryMiddleware - {TraceId}", context.FrontendRequest.TraceId);

        int? offset = null;
        int? limit = null;
        bool totalCount = false;
        List<string> errors = [];

        if (context.FrontendRequest.QueryParameters.ContainsKey("offset"))
        {
            if (
                !int.TryParse(context.FrontendRequest.QueryParameters["offset"], out int offsetVal)
                || offsetVal < 0
            )
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
            if (
                !int.TryParse(context.FrontendRequest.QueryParameters["limit"], out int limitVal)
                || limitVal < 0
            )
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
                totalCount = bool.TryParse(
                    context.FrontendRequest.QueryParameters["totalCount"],
                    out bool totalValue
                )
                    ? totalValue
                    : totalCount;
            }
        }

        // TODO: DMS-312 Validate all other query parameters are valid ones for the current resource

        if (errors.Count > 0)
        {
            FailureResponseWithErrors failureResponse = FailureResponse.ForBadRequest(
                "The request could not be processed. See 'errors' for details.",
                context.FrontendRequest.TraceId,
                [],
                errors.ToArray()
            );

            _logger.LogDebug(
                "'{Status}'.'{EndpointName}' - {TraceId}",
                failureResponse.status.ToString(),
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId
            );

            context.FrontendResponse = new FrontendResponse(
                failureResponse.status,
                SerializeBody(failureResponse),
                []
            );
            return;
        }

        context.PaginationParameters = new PaginationParameters(limit, offset, totalCount);

        context.TermQueries = context
            .FrontendRequest.QueryParameters.ExceptBy(_disallowedQueryFields, (term) => term.Key)
            .Select(term => new TermQuery(term.Key, term.Value))
            .ToArray();

        await next();
    }
}
