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
/// EdOrg ids from the payload to the requestData EducationOrganizationHierarchyInfo
/// for authorization challenges later in the request pipeline.
/// </summary>
internal class ProvideEducationOrganizationHierarchyMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ProvideEducationOrganizationHierarchyMiddleware - {TraceId}",
            requestData.FrontendRequest.TraceId.Value
        );

        // Skip this logic if the path contains "homograph/schools"
        if (requestData.FrontendRequest.Path.Contains("homograph/schools"))
        {
            _logger.LogDebug(
                "Skipping Provide EducationOrganization Hierarchy Middleware for /homograph/schools Resource  - {TraceId}",
                requestData.FrontendRequest.TraceId.Value
            );
        }
        else
        {
            requestData.EducationOrganizationHierarchyInfo = GetHierarchyInfo(requestData);
        }

        await next();
    }

    private EducationOrganizationHierarchyInfo GetHierarchyInfo(RequestData requestData)
    {
        bool isEdOrgHierarchy = requestData.ProjectSchema.EducationOrganizationTypes.Contains(
            requestData.ResourceSchema.ResourceName
        );

        if (!isEdOrgHierarchy)
        {
            _logger.LogDebug(
                "Resource {ResourceName} IS NOT in EducationOrganizationHierarchy - {TraceId}",
                requestData.ResourceSchema.ResourceName,
                requestData.FrontendRequest.TraceId.Value
            );
            return new EducationOrganizationHierarchyInfo(false, default, default);
        }

        long educationOrganizationId = ExtractEducationOrganizationId(requestData);
        long? parentId = FindParentEducationOrganizationId(requestData);

        _logger.LogDebug(
            "Resource {ResourceName} with Id: {Id} IS in EducationOrganizationHierarchy and has parentId: [{ParentIds}] - {TraceId}",
            requestData.ResourceSchema.ResourceName.Value,
            educationOrganizationId,
            string.Join(',', parentId),
            requestData.FrontendRequest.TraceId.Value
        );

        return new EducationOrganizationHierarchyInfo(true, educationOrganizationId, parentId);
    }

    private long ExtractEducationOrganizationId(RequestData requestData)
    {
        (DocumentIdentity documentIdentity, _) = requestData.ResourceSchema.ExtractIdentities(
            requestData.ParsedBody,
            logger: _logger
        );

        return long.Parse(documentIdentity.DocumentIdentityElements[0].IdentityValue);
    }

    private long? FindParentEducationOrganizationId(RequestData requestData)
    {
        if (requestData.DocumentSecurityElements?.EducationOrganization == null)
        {
            return default;
        }

        var parentPaths = requestData.ProjectSchema.EducationOrganizationHierarchy[
            requestData.ResourceSchema.ResourceName
        ];

        return parentPaths
            .SelectMany(parentPath =>
                requestData
                    .ParsedBody.SelectNodesFromArrayPathCoerceToStrings(parentPath, _logger)
                    .Select(long.Parse)
            )
            .Distinct()
            .FirstOrDefault();
    }
}
