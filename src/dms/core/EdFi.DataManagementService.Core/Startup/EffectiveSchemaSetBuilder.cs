// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend.RelationalModel.Schema;

/// <summary>
/// Bridges normalized <see cref="ApiSchemaDocumentNodes" /> input to the backend-facing
/// <see cref="EffectiveSchemaSet" /> consumed by startup, DDL, and runtime compilation workflows.
///
/// The returned <see cref="EffectiveSchemaSet" /> contains <see cref="EffectiveProjectSchema" />
/// instances whose <c>ProjectSchema</c> JsonObjects share references with the input nodes.
/// Callers that need isolated copies should deep-clone the schema set before passing it
/// to components that mutate <c>ProjectSchema</c> nodes.
/// </summary>
public sealed class EffectiveSchemaSetBuilder(
    IEffectiveSchemaHashProvider hashProvider,
    IResourceKeySeedProvider seedProvider
)
{
    /// <summary>
    /// Builds an <see cref="EffectiveSchemaSet" /> from normalized API schema nodes.
    /// </summary>
    public EffectiveSchemaSet Build(ApiSchemaDocumentNodes nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var apiSchemaFormatVersion = EffectiveSchemaHashProvider.GetApiSchemaVersion(
            nodes.CoreApiSchemaRootNode
        );

        var sortedMetadata = new[] { nodes.CoreApiSchemaRootNode }
            .Concat(nodes.ExtensionApiSchemaRootNodes)
            .Select(ProjectSchemaMetadataExtractor.Extract)
            .OrderBy(metadata => metadata.ProjectEndpointName, StringComparer.Ordinal)
            .ToList();

        var effectiveSchemaHash = hashProvider.ComputeHash(apiSchemaFormatVersion, sortedMetadata);

        var pairedProjectsAndComponents = sortedMetadata.Select(ToProjectAndComponent).ToList();
        var projectsInEndpointOrder = pairedProjectsAndComponents.Select(pair => pair.Project).ToList();
        var componentsInEndpointOrder = pairedProjectsAndComponents.Select(pair => pair.Component).ToArray();

        var seeds = seedProvider.GetSeeds(nodes);
        var seedHash = seedProvider.ComputeSeedHash(seeds);

        var resourceKeys = seeds
            .Select(seed => new ResourceKeyEntry(
                seed.ResourceKeyId,
                new QualifiedResourceName(seed.ProjectName, seed.ResourceName),
                seed.ResourceVersion,
                seed.IsAbstract
            ))
            .ToArray();

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

    private static (EffectiveProjectSchema Project, SchemaComponentInfo Component) ToProjectAndComponent(
        ProjectSchemaMetadata metadata
    )
    {
        var project = new EffectiveProjectSchema(
            metadata.ProjectEndpointName,
            metadata.ProjectName,
            metadata.ProjectVersion,
            metadata.IsExtensionProject,
            metadata.ProjectSchema
        );

        var component = new SchemaComponentInfo(
            metadata.ProjectEndpointName,
            metadata.ProjectName,
            metadata.ProjectVersion,
            metadata.IsExtensionProject,
            metadata.ProjectHash
        );

        return (project, component);
    }
}
