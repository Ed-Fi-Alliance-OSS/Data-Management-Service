// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Builds the ResourceInfo to pass to the backends
/// </summary>
internal class BuildResourceInfoMiddleware(ILogger _logger, List<string> _allowIdentityUpdateOverrides)
    : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering BuildResourceInfoMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.ResourceInfo = new(
            ProjectName: requestInfo.ProjectSchema.ProjectName,
            ResourceVersion: requestInfo.ProjectSchema.ResourceVersion,
            ResourceName: requestInfo.ResourceSchema.ResourceName,
            IsDescriptor: requestInfo.ResourceSchema.IsDescriptor,
            AllowIdentityUpdates: requestInfo.ResourceSchema.AllowIdentityUpdates
                || _allowIdentityUpdateOverrides.Contains(requestInfo.ResourceSchema.ResourceName.Value),
            EducationOrganizationHierarchyInfo: requestInfo.EducationOrganizationHierarchyInfo,
            AuthorizationSecurableInfo: requestInfo.AuthorizationSecurableInfo
        );

        await next();
    }
}
