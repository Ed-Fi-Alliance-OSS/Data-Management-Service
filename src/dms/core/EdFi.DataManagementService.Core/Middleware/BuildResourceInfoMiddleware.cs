// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Builds the ResourceInfo to pass to the backends
/// </summary>
internal class BuildResourceInfoMiddleware(ILogger _logger, List<string> _allowIdentityUpdateOverrides) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering BuildResourceInfoMiddleware - {TraceId}", context.FrontendRequest.TraceId);

        context.ResourceInfo = new(
            ProjectName: context.ProjectSchema.ProjectName,
            ResourceVersion: context.ProjectSchema.ResourceVersion,
            ResourceName: context.ResourceSchema.ResourceName,
            IsDescriptor: context.ResourceSchema.IsDescriptor,
            AllowIdentityUpdates: context.ResourceSchema.AllowIdentityUpdates || _allowIdentityUpdateOverrides.Contains(context.ResourceSchema.ResourceName.Value)
        );

        await next();
    }
}
