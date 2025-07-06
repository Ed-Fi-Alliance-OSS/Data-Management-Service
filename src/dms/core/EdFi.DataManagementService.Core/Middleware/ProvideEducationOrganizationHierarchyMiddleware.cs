// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// This middleware determines if the resource in the request is a type of
/// EducationOrganization, and if so it provides the EdOrg id and any parent
/// EdOrg ids from the payload to the requestInfo EducationOrganizationHierarchyInfo
/// for authorization challenges later in the request pipeline.
/// </summary>
internal class ProvideEducationOrganizationHierarchyMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ProvideEducationOrganizationHierarchyMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Skip this logic if the path contains "homograph/schools"
        if (requestInfo.FrontendRequest.Path.Contains("homograph/schools"))
        {
            _logger.LogDebug(
                "Skipping Provide EducationOrganization Hierarchy Middleware for /homograph/schools Resource  - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
        }
        else
        {
            requestInfo.EducationOrganizationHierarchyInfo = GetHierarchyInfo(requestInfo);
        }

        await next();
    }

    private EducationOrganizationHierarchyInfo GetHierarchyInfo(RequestInfo requestInfo)
    {
        bool isEdOrgHierarchy = requestInfo.ProjectSchema.EducationOrganizationTypes.Contains(
            requestInfo.ResourceSchema.ResourceName
        );

        if (!isEdOrgHierarchy)
        {
            _logger.LogDebug(
                "Resource {ResourceName} IS NOT in EducationOrganizationHierarchy - {TraceId}",
                requestInfo.ResourceSchema.ResourceName,
                requestInfo.FrontendRequest.TraceId.Value
            );
            return new EducationOrganizationHierarchyInfo(false, default, default);
        }

        long educationOrganizationId = ExtractEducationOrganizationId(requestInfo);
        long? parentId = FindParentEducationOrganizationId(requestInfo);

        _logger.LogDebug(
            "Resource {ResourceName} with Id: {Id} IS in EducationOrganizationHierarchy and has parentId: [{ParentIds}] - {TraceId}",
            requestInfo.ResourceSchema.ResourceName.Value,
            educationOrganizationId,
            string.Join(',', parentId),
            requestInfo.FrontendRequest.TraceId.Value
        );

        return new EducationOrganizationHierarchyInfo(true, educationOrganizationId, parentId);
    }

    private long ExtractEducationOrganizationId(RequestInfo requestInfo)
    {
        (DocumentIdentity documentIdentity, _) = requestInfo.ResourceSchema.ExtractIdentities(
            requestInfo.ParsedBody,
            logger: _logger
        );

        return long.Parse(documentIdentity.DocumentIdentityElements[0].IdentityValue);
    }

    private long? FindParentEducationOrganizationId(RequestInfo requestInfo)
    {
        if (requestInfo.DocumentSecurityElements?.EducationOrganization == null)
        {
            return default;
        }

        var parentPaths = requestInfo.ProjectSchema.EducationOrganizationHierarchy[
            requestInfo.ResourceSchema.ResourceName
        ];

        return parentPaths
            .SelectMany(parentPath =>
                requestInfo
                    .ParsedBody.SelectNodesFromArrayPathCoerceToStrings(parentPath, _logger)
                    .Select(long.Parse)
            )
            .Distinct()
            .FirstOrDefault();
    }
}
