// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.SchemaTools.Bridge;

/// <summary>
/// Bridges <see cref="ApiSchemaDocumentNodes"/> (file-loader output)
/// to <see cref="EffectiveSchemaSet"/> (DDL pipeline input).
/// </summary>
public sealed class EffectiveSchemaSetBuilder(
    IEffectiveSchemaHashProvider hashProvider,
    IResourceKeySeedProvider seedProvider
)
{
    /// <summary>
    /// Builds an <see cref="EffectiveSchemaSet"/> from normalized API schema nodes.
    /// </summary>
    public EffectiveSchemaSet Build(ApiSchemaDocumentNodes nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        // Step 1: Compute the effective schema hash
        var effectiveSchemaHash = hashProvider.ComputeHash(nodes);

        // Step 2: Extract apiSchemaFormatVersion from core schema
        var apiSchemaFormatVersion =
            nodes.CoreApiSchemaRootNode["apiSchemaVersion"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Core schema node missing 'apiSchemaVersion'");

        // Step 3: Extract project schemas (core + extensions) and build EffectiveProjectSchema list
        var projects = new List<EffectiveProjectSchema>(1 + nodes.ExtensionApiSchemaRootNodes.Length);
        var schemaComponents = new List<SchemaComponentInfo>(1 + nodes.ExtensionApiSchemaRootNodes.Length);

        // Process core schema
        var (coreProject, coreComponent) = ToProjectAndComponent(
            ProjectSchemaMetadataExtractor.Extract(nodes.CoreApiSchemaRootNode)
        );
        projects.Add(coreProject);
        schemaComponents.Add(coreComponent);

        // Process extension schemas
        foreach (var extensionNode in nodes.ExtensionApiSchemaRootNodes)
        {
            var (extensionProject, extensionComponent) = ToProjectAndComponent(
                ProjectSchemaMetadataExtractor.Extract(extensionNode)
            );
            projects.Add(extensionProject);
            schemaComponents.Add(extensionComponent);
        }

        // Step 4: Sort projects and components by endpoint name
        var projectsInEndpointOrder = projects
            .OrderBy(p => p.ProjectEndpointName, StringComparer.Ordinal)
            .ToList();

        var componentsInEndpointOrder = schemaComponents
            .OrderBy(c => c.ProjectEndpointName, StringComparer.Ordinal)
            .ToArray();

        // Step 5: Get resource key seeds and compute seed hash
        var seeds = seedProvider.GetSeeds(nodes);
        var seedHash = seedProvider.ComputeSeedHash(seeds);

        // Step 6: Map Core ResourceKeySeed → Backend.External ResourceKeyEntry
        var resourceKeys = seeds
            .Select(seed => new ResourceKeyEntry(
                seed.ResourceKeyId,
                new QualifiedResourceName(seed.ProjectName, seed.ResourceName),
                seed.ResourceVersion,
                seed.IsAbstract
            ))
            .ToArray();

        // Step 7: Assemble EffectiveSchemaInfo
        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            apiSchemaFormatVersion,
            SchemaHashConstants.RelationalMappingVersion,
            effectiveSchemaHash,
            resourceKeys.Length,
            seedHash,
            componentsInEndpointOrder,
            resourceKeys
        );

        return new EffectiveSchemaSet(effectiveSchemaInfo, projectsInEndpointOrder);
    }

    /// <summary>
    /// Converts shared <see cref="ProjectSchemaMetadata"/> into the domain types needed
    /// by the DDL pipeline, deep-cloning the <c>ProjectSchema</c> to detach it from its parent node.
    /// </summary>
    private static (EffectiveProjectSchema Project, SchemaComponentInfo Component) ToProjectAndComponent(
        ProjectSchemaMetadata meta
    )
    {
        // Deep-clone the projectSchema for the EffectiveProjectSchema (detach from parent)
        var detachedSchema =
            meta.ProjectSchema.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("projectSchema deep clone must produce a JsonObject");

        var project = new EffectiveProjectSchema(
            meta.ProjectEndpointName,
            meta.ProjectName,
            meta.ProjectVersion,
            meta.IsExtensionProject,
            detachedSchema
        );

        var component = new SchemaComponentInfo(
            meta.ProjectEndpointName,
            meta.ProjectName,
            meta.ProjectVersion,
            meta.IsExtensionProject,
            meta.ProjectHash
        );

        return (project, component);
    }
}
