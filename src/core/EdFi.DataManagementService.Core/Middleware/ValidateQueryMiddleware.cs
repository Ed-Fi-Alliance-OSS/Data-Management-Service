// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Pipeline;
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

        if (context.FrontendRequest.QueryParameters.ContainsKey("offset"))
        {
            offset = int.TryParse(context.FrontendRequest.QueryParameters["offset"], out int offsetResult) ? offsetResult : offset;
        }
        if (context.FrontendRequest.QueryParameters.ContainsKey("limit"))
        {
            limit = int.TryParse(context.FrontendRequest.QueryParameters["limit"], out int limitResult) ? limitResult : limit;
        }
        if (context.FrontendRequest.QueryParameters.ContainsKey("totalCount"))
        {
            totalCount = bool.TryParse(context.FrontendRequest.QueryParameters["totalCount"], out bool totalValue) ? totalValue : totalCount;
        }
        
        context.PaginationParameters = new(limit, offset);


        await next();
    }
}

