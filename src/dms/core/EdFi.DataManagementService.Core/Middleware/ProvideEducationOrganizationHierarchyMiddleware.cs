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
/// EdOrg ids from the payload to the context EducationOrganizationHierarchyInfo
/// for authorization challenges later in the request pipeline.
/// </summary>
internal class ProvideEducationOrganizationHierarchyMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ProvideEducationOrganizationHierarchyMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        // Skip this logic if the path contains "homograph/schools"
        if (context.FrontendRequest.Path.Contains("homograph/schools"))
        {
            _logger.LogDebug(
                "Skipping Provide EducationOrganization Hierarchy Middleware for /homograph/schools Resource  - {TraceId}",
                context.FrontendRequest.TraceId.Value
            );
        }
        else
        {
            context.EducationOrganizationHierarchyInfo = GetHierarchyInfo(context);
        }

        await next();
    }

    private EducationOrganizationHierarchyInfo GetHierarchyInfo(PipelineContext context)
    {
        bool isEdOrgHierarchy = context.ProjectSchema.EducationOrganizationTypes.Contains(
            context.ResourceSchema.ResourceName
        );

        if (!isEdOrgHierarchy)
        {
            _logger.LogDebug(
                "Resource {ResourceName} IS NOT in EducationOrganizationHierarchy - {TraceId}",
                context.ResourceSchema.ResourceName,
                context.FrontendRequest.TraceId.Value
            );
            return new EducationOrganizationHierarchyInfo(false, default, default);
        }

        long educationOrganizationId = ExtractEducationOrganizationId(context);
        long? parentId = FindParentEducationOrganizationId(context);

        _logger.LogDebug(
            "Resource {ResourceName} with Id: {Id} IS in EducationOrganizationHierarchy and has parentId: [{ParentIds}] - {TraceId}",
            context.ResourceSchema.ResourceName.Value,
            educationOrganizationId,
            string.Join(',', parentId),
            context.FrontendRequest.TraceId.Value
        );

        return new EducationOrganizationHierarchyInfo(true, educationOrganizationId, parentId);
    }

    private long ExtractEducationOrganizationId(PipelineContext context)
    {
        (DocumentIdentity documentIdentity, _) = context.ResourceSchema.ExtractIdentities(
            context.ParsedBody,
            logger: _logger
        );

        return long.Parse(documentIdentity.DocumentIdentityElements[0].IdentityValue);
    }

    private long? FindParentEducationOrganizationId(PipelineContext context)
    {
        if (context.DocumentSecurityElements?.EducationOrganization == null)
        {
            return default;
        }

        var parentPaths = context.ProjectSchema.EducationOrganizationHierarchy[
            context.ResourceSchema.ResourceName
        ];

        return parentPaths
            .SelectMany(parentPath =>
                context
                    .ParsedBody.SelectNodesFromArrayPathCoerceToStrings(parentPath, _logger)
                    .Select(long.Parse)
            )
            .Distinct()
            .FirstOrDefault();
    }
}
