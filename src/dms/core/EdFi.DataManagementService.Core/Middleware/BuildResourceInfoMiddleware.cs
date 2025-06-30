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
    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering BuildResourceInfoMiddleware - {TraceId}",
            requestData.FrontendRequest.TraceId.Value
        );

        requestData.ResourceInfo = new(
            ProjectName: requestData.ProjectSchema.ProjectName,
            ResourceVersion: requestData.ProjectSchema.ResourceVersion,
            ResourceName: requestData.ResourceSchema.ResourceName,
            IsDescriptor: requestData.ResourceSchema.IsDescriptor,
            AllowIdentityUpdates: requestData.ResourceSchema.AllowIdentityUpdates
                || _allowIdentityUpdateOverrides.Contains(requestData.ResourceSchema.ResourceName.Value),
            EducationOrganizationHierarchyInfo: requestData.EducationOrganizationHierarchyInfo,
            AuthorizationSecurableInfo: requestData.AuthorizationSecurableInfo
        );

        await next();
    }
}
