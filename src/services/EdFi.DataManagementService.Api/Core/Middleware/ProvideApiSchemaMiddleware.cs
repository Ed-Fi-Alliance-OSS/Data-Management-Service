// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core.Middleware;

public class ProvideApiSchemaMiddleware(IApiSchemaProvider _apiSchemaProvider, ILogger _logger)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ProvideApiSchemaMiddleware- {TraceId}", context.FrontendRequest.TraceId);

        var document = _apiSchemaProvider.ApiSchemaRootNode;
        context.ApiSchemaDocument = new ApiSchemaDocument(document, _logger);
        await next();
    }
}
