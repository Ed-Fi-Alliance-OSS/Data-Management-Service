// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.SchemaTools.Bridge;

/// <summary>
/// Bridges <see cref="ApiSchemaDocumentNodes"/> (file-loader output)
/// to <see cref="EffectiveSchemaSet"/> (DDL pipeline input).
///
/// The returned <see cref="EffectiveSchemaSet"/> contains <see cref="EffectiveProjectSchema"/>
/// instances whose <c>ProjectSchema</c> JsonObjects share references with the input nodes.
/// Callers that need isolated copies (e.g., for mutation by the relational model builder)
/// should deep-clone the schema set before use.
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

        // Step 1: Extract apiSchemaFormatVersion from core schema
        var apiSchemaFormatVersion = EffectiveSchemaHashProvider.GetApiSchemaVersion(
            nodes.CoreApiSchemaRootNode
        );

        // Step 2: Extract project metadata once (core + extensions), sorted by endpoint name
        var allNodes = new[] { nodes.CoreApiSchemaRootNode }.Concat(nodes.ExtensionApiSchemaRootNodes);

        var sortedMetadata = allNodes
            .Select(ProjectSchemaMetadataExtractor.Extract)
            .OrderBy(m => m.ProjectEndpointName, StringComparer.Ordinal)
            .ToList();

        // Step 3: Compute the effective schema hash using pre-extracted metadata
        var effectiveSchemaHash = hashProvider.ComputeHash(apiSchemaFormatVersion, sortedMetadata);

        // Step 4: Build paired project/component lists from the same metadata
        var pairs = sortedMetadata.Select(ToProjectAndComponent).ToList();
        var projectsInEndpointOrder = pairs.Select(p => p.Project).ToList();
        var componentsInEndpointOrder = pairs.Select(p => p.Component).ToArray();

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
    /// by the DDL pipeline. The <c>ProjectSchema</c> is NOT deep-cloned here; it retains
    /// a reference to the original node. Callers that need mutation-safe copies should
    /// deep-clone the returned <see cref="EffectiveSchemaSet"/> before passing it to
    /// components that mutate <c>ProjectSchema</c> (e.g., the relational model builder).
    /// </summary>
    private static (EffectiveProjectSchema Project, SchemaComponentInfo Component) ToProjectAndComponent(
        ProjectSchemaMetadata meta
    )
    {
        var project = new EffectiveProjectSchema(
            meta.ProjectEndpointName,
            meta.ProjectName,
            meta.ProjectVersion,
            meta.IsExtensionProject,
            meta.ProjectSchema
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
