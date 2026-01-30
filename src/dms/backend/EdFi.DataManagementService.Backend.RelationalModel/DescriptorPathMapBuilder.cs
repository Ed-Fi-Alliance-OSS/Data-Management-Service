// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace EdFi.DataManagementService.Backend.RelationalModel;

internal sealed record DescriptorPathMapResult(
    IReadOnlyDictionary<
        QualifiedResourceName,
        IReadOnlyDictionary<string, DescriptorPathInfo>
    > BaseDescriptorPathsByResource,
    IReadOnlyDictionary<
        QualifiedResourceName,
        IReadOnlyDictionary<string, DescriptorPathInfo>
    > ExtensionDescriptorPathsByResource
);

internal sealed class DescriptorPathMapBuilder
{
    public DescriptorPathMapResult Build(IReadOnlyList<ProjectSchemaContext> projectsInEndpointOrder)
    {
        if (projectsInEndpointOrder.Count == 0)
        {
            return new DescriptorPathMapResult(
                new Dictionary<QualifiedResourceName, IReadOnlyDictionary<string, DescriptorPathInfo>>(),
                new Dictionary<QualifiedResourceName, IReadOnlyDictionary<string, DescriptorPathInfo>>()
            );
        }

        var projectSchemas = projectsInEndpointOrder
            .Select(project => new DescriptorPathInference.ProjectDescriptorSchema(
                project.ProjectSchema.ProjectName,
                project.EffectiveProject.ProjectSchema
            ))
            .ToArray();

        var descriptorPathsByResource = DescriptorPathInference.BuildDescriptorPathsByResource(
            projectSchemas
        );

        Dictionary<
            QualifiedResourceName,
            IReadOnlyDictionary<string, DescriptorPathInfo>
        > baseDescriptorPathsByResource = new();
        Dictionary<
            QualifiedResourceName,
            IReadOnlyDictionary<string, DescriptorPathInfo>
        > extensionDescriptorPathsByResource = new();

        foreach (var resourceEntry in descriptorPathsByResource)
        {
            Dictionary<string, DescriptorPathInfo> basePaths = new(StringComparer.Ordinal);
            Dictionary<string, DescriptorPathInfo> extensionPaths = new(StringComparer.Ordinal);

            foreach (var pathEntry in resourceEntry.Value)
            {
                if (IsExtensionScoped(pathEntry.Value.DescriptorValuePath))
                {
                    extensionPaths.Add(pathEntry.Key, pathEntry.Value);
                    continue;
                }

                basePaths.Add(pathEntry.Key, pathEntry.Value);
            }

            baseDescriptorPathsByResource[resourceEntry.Key] = basePaths;
            extensionDescriptorPathsByResource[resourceEntry.Key] = extensionPaths;
        }

        return new DescriptorPathMapResult(baseDescriptorPathsByResource, extensionDescriptorPathsByResource);
    }

    private static bool IsExtensionScoped(JsonPathExpression path)
    {
        return path.Segments.Any(segment => segment is JsonPathSegment.Property { Name: "_ext" });
    }
}
