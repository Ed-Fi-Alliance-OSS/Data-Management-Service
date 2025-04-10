// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Provides resource authorization securable info from resource schema.
/// </summary>
/// <param name="_logger"></param>
internal class ProvideAuthorizationSecurableInfoMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ProvideAuthorizationSecurableInfoMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        context.AuthorizationSecurableInfo = context.ResourceSchema.ExtractAuthorizationSecurableInfo();
        await next();
    }
}
