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

        bool isEdOrgHierarchy = context
            .ProjectSchema.EducationOrganizationHierarchy["EducationOrganization"]
            .Contains(context.ResourceInfo.ResourceName.Value);
        string educationOrganizationId = "";
        string? parentEducationOrganizationId = null;

        if (isEdOrgHierarchy)
        {
            var (documentIdentity, _) = context.ResourceSchema.ExtractIdentities(
                context.ParsedBody,
                _logger
            );
            educationOrganizationId = documentIdentity.DocumentIdentityElements[0].IdentityValue;
            parentEducationOrganizationId = null;
        }

        context.EducationOrganizationHierarchyInfo = new EducationOrganizationHierarchyInfo(
            isEdOrgHierarchy,
            educationOrganizationId,
            parentEducationOrganizationId
        );

        await next();
    }
}
