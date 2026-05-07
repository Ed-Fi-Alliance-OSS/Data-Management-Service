// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public sealed record DescriptorReadSeed(
    DocumentUuid DocumentUuid,
    string Namespace,
    string CodeValue,
    string ShortDescription,
    string? Description = null,
    DateOnly? EffectiveBeginDate = null,
    DateOnly? EffectiveEndDate = null,
    string? Discriminator = null
)
{
    public string Uri => $"{Namespace}#{CodeValue}";
}

public static class DescriptorReadIntegrationTestSupport
{
    private static readonly IResourceAuthorizationHandler AllowAllResourceAuthorizationHandler =
        new DescriptorReadAllowAllResourceAuthorizationHandler();

    public static ResourceInfo CreateResourceInfo(
        EffectiveSchemaSet effectiveSchemaSet,
        string projectEndpointName,
        string resourceName
    )
    {
        ArgumentNullException.ThrowIfNull(effectiveSchemaSet);

        var (projectSchema, resourceSchema) = GetResourceSchema(
            effectiveSchemaSet,
            projectEndpointName,
            resourceName
        );

        return new(
            ProjectName: projectSchema.ProjectName,
            ResourceName: resourceSchema.ResourceName,
            IsDescriptor: resourceSchema.IsDescriptor,
            ResourceVersion: projectSchema.ResourceVersion,
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );
    }

    public static short GetDescriptorResourceKeyIdOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (!mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            throw new InvalidOperationException(
                $"Resource '{FormatResource(resource)}' is not stored in the shared descriptor table."
            );
        }

        if (mappingSet.ResourceKeyIdByResource.TryGetValue(resource, out var resourceKeyId))
        {
            return resourceKeyId;
        }

        throw new InvalidOperationException(
            $"Mapping set '{FormatMappingSetKey(mappingSet.Key)}' does not contain a ResourceKeyId entry "
                + $"for descriptor resource '{FormatResource(resource)}'."
        );
    }

    public static IntegrationRelationalGetRequest CreateGetRequest(
        DocumentUuid documentUuid,
        ResourceInfo resourceInfo,
        MappingSet mappingSet,
        TraceId traceId,
        RelationalGetRequestReadMode readMode = RelationalGetRequestReadMode.ExternalResponse,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null,
        AuthorizationStrategyEvaluator[]? authorizationStrategyEvaluators = null
    )
    {
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(mappingSet);

        return new(
            DocumentUuid: documentUuid,
            ResourceInfo: resourceInfo,
            MappingSet: mappingSet,
            ResourceAuthorizationHandler: AllowAllResourceAuthorizationHandler,
            AuthorizationStrategyEvaluators: authorizationStrategyEvaluators ?? [],
            TraceId: traceId,
            ReadMode: readMode,
            ReadableProfileProjectionContext: readableProfileProjectionContext
        );
    }

    private static (ProjectSchema ProjectSchema, ResourceSchema ResourceSchema) GetResourceSchema(
        EffectiveSchemaSet effectiveSchemaSet,
        string projectEndpointName,
        string resourceName
    )
    {
        var effectiveProjectSchema = effectiveSchemaSet.ProjectsInEndpointOrder.Single(project =>
            string.Equals(
                project.ProjectEndpointName,
                projectEndpointName,
                StringComparison.OrdinalIgnoreCase
            )
        );

        var projectSchema = new ProjectSchema(effectiveProjectSchema.ProjectSchema, NullLogger.Instance);
        var resourceSchemaNode =
            projectSchema.FindResourceSchemaNodeByResourceName(new ResourceName(resourceName))
            ?? projectSchema
                .GetAllResourceSchemaNodes()
                .SingleOrDefault(node =>
                    string.Equals(
                        node["resourceName"]?.GetValue<string>(),
                        resourceName,
                        StringComparison.Ordinal
                    )
                )
            ?? throw new InvalidOperationException(
                $"Could not find resource '{resourceName}' in project '{projectEndpointName}'."
            );

        return (projectSchema, new ResourceSchema(resourceSchemaNode));
    }

    private static string FormatResource(QualifiedResourceName resource) =>
        $"{resource.ProjectName}.{resource.ResourceName}";

    private static string FormatMappingSetKey(MappingSetKey key) =>
        $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
}

file sealed class DescriptorReadAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}
