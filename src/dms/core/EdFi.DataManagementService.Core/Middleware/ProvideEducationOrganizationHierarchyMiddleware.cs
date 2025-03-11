// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class ProvideEducationOrganizationHierarchyMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ProvideEducationOrganizationHierarchyMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        context.EducationOrganizationHierarchyInfo = GetHierarchyInfo(context);

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
            return new EducationOrganizationHierarchyInfo(false, default, []);
        }

        long educationOrganizationId = ExtractEducationOrganizationId(context);
        long[] parentIds = FindParentEducationOrganizationIds(context);

        _logger.LogDebug(
            "Resource {ResourceName} with Id: {Id} IS in EducationOrganizationHierarchy and has parentIds: [{ParentIds}] - {TraceId}",
            context.ResourceSchema.ResourceName.Value,
            educationOrganizationId,
            string.Join(',', parentIds),
            context.FrontendRequest.TraceId.Value
        );

        return new EducationOrganizationHierarchyInfo(true, educationOrganizationId, parentIds);
    }

    private long ExtractEducationOrganizationId(PipelineContext context)
    {
        (DocumentIdentity documentIdentity, _) = context.ResourceSchema.ExtractIdentities(
            context.ParsedBody,
            logger: _logger
        );

        return long.Parse(documentIdentity.DocumentIdentityElements[0].IdentityValue);
    }

    private static long[] FindParentEducationOrganizationIds(PipelineContext context)
    {
        if (context.DocumentSecurityElements?.EducationOrganization == null)
        {
            return [];
        }

        var parentTypes = context.ProjectSchema.EducationOrganizationHierarchy[
            context.ResourceSchema.ResourceName
        ];

        return context
            .DocumentSecurityElements.EducationOrganization.Where(e => parentTypes.Contains(e.ResourceName))
            .Select(e => e.Id.Value)
            .ToArray();
    }
}
