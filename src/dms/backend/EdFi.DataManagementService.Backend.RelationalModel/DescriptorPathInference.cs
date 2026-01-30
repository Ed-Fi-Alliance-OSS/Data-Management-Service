// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Builds descriptor path metadata across one or more project schemas.
/// </summary>
internal static class DescriptorPathInference
{
    /// <summary>
    /// Provides project-level inputs required for descriptor path inference.
    /// </summary>
    /// <param name="ProjectName">The project name used for qualified resource names.</param>
    /// <param name="ProjectSchema">The project schema payload.</param>
    internal sealed record ProjectDescriptorSchema(string ProjectName, JsonObject ProjectSchema);

    /// <summary>
    /// Builds descriptor path inventories for all resources across the supplied projects.
    /// </summary>
    public static Dictionary<
        QualifiedResourceName,
        Dictionary<string, DescriptorPathInfo>
    > BuildDescriptorPathsByResource(IReadOnlyList<ProjectDescriptorSchema> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        if (projects.Count == 0)
        {
            return new Dictionary<QualifiedResourceName, Dictionary<string, DescriptorPathInfo>>();
        }

        Dictionary<QualifiedResourceName, Dictionary<string, DescriptorPathInfo>> descriptorPathsByResource =
            new();
        Dictionary<QualifiedResourceName, List<ReferenceJsonPathInfo>> referenceJsonPathsByResource = new();

        var orderedProjects = projects
            .OrderBy(project => project?.ProjectName, StringComparer.Ordinal)
            .ToArray();

        foreach (var project in orderedProjects)
        {
            if (project is null)
            {
                throw new InvalidOperationException(
                    "Project descriptor schemas must not contain null entries."
                );
            }

            var projectName = RequireNonEmpty(project.ProjectName, "ProjectName");
            var projectSchema =
                project.ProjectSchema
                ?? throw new InvalidOperationException("ProjectSchema must be provided.");

            var resourceSchemas = RequireObject(
                projectSchema["resourceSchemas"],
                "projectSchema.resourceSchemas"
            );
            AddResourceDescriptors(
                resourceSchemas,
                projectName,
                descriptorPathsByResource,
                referenceJsonPathsByResource,
                "projectSchema.resourceSchemas"
            );

            if (projectSchema["abstractResources"] is JsonObject abstractResources)
            {
                AddResourceDescriptors(
                    abstractResources,
                    projectName,
                    descriptorPathsByResource,
                    referenceJsonPathsByResource,
                    "projectSchema.abstractResources"
                );
            }
        }

        PropagateDescriptorPaths(descriptorPathsByResource, referenceJsonPathsByResource);

        return descriptorPathsByResource;
    }

    /// <summary>
    /// Represents a reference mapping used to propagate descriptor paths between resources.
    /// </summary>
    private sealed record ReferenceJsonPathInfo(
        QualifiedResourceName ReferencedResource,
        JsonPathExpression IdentityPath,
        JsonPathExpression ReferencePath
    );

    /// <summary>
    /// Propagates descriptor paths from referenced resources to reference paths until a fixed point is reached.
    /// </summary>
    /// <param name="descriptorPathsByResource">Descriptor paths keyed by resource.</param>
    /// <param name="referenceJsonPathsByResource">
    /// Reference path metadata keyed by resource, used to determine where propagation should occur.
    /// </param>
    private static void PropagateDescriptorPaths(
        Dictionary<QualifiedResourceName, Dictionary<string, DescriptorPathInfo>> descriptorPathsByResource,
        Dictionary<QualifiedResourceName, List<ReferenceJsonPathInfo>> referenceJsonPathsByResource
    )
    {
        var updated = true;

        while (updated)
        {
            updated = false;

            var orderedResources = referenceJsonPathsByResource
                .Keys.OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
                .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
                .ToArray();

            foreach (var resourceKey in orderedResources)
            {
                var descriptorPaths = descriptorPathsByResource[resourceKey];
                var orderedReferenceInfos = referenceJsonPathsByResource[resourceKey]
                    .OrderBy(info => info.ReferencedResource.ProjectName, StringComparer.Ordinal)
                    .ThenBy(info => info.ReferencedResource.ResourceName, StringComparer.Ordinal)
                    .ThenBy(info => info.IdentityPath.Canonical, StringComparer.Ordinal)
                    .ThenBy(info => info.ReferencePath.Canonical, StringComparer.Ordinal)
                    .ToArray();

                foreach (var referenceInfo in orderedReferenceInfos)
                {
                    if (
                        !descriptorPathsByResource.TryGetValue(
                            referenceInfo.ReferencedResource,
                            out var referencedDescriptorPaths
                        )
                    )
                    {
                        continue;
                    }

                    if (
                        !referencedDescriptorPaths.TryGetValue(
                            referenceInfo.IdentityPath.Canonical,
                            out var descriptorPathInfo
                        )
                    )
                    {
                        continue;
                    }

                    var referencePath = referenceInfo.ReferencePath;

                    if (descriptorPaths.TryGetValue(referencePath.Canonical, out var existingInfo))
                    {
                        if (existingInfo.DescriptorResource != descriptorPathInfo.DescriptorResource)
                        {
                            throw new InvalidOperationException(
                                $"Descriptor path '{referencePath.Canonical}' is already defined."
                            );
                        }

                        continue;
                    }

                    descriptorPaths.Add(
                        referencePath.Canonical,
                        new DescriptorPathInfo(referencePath, descriptorPathInfo.DescriptorResource)
                    );
                    updated = true;
                }
            }
        }
    }

    /// <summary>
    /// Adds descriptor path inventories and reference propagation metadata for each resource in the provided schema set.
    /// </summary>
    /// <param name="resourceSchemas">The <c>resourceSchemas</c> or <c>abstractResources</c> object.</param>
    /// <param name="projectName">The logical project name.</param>
    /// <param name="descriptorPathsByResource">The target dictionary for descriptor paths.</param>
    /// <param name="referenceJsonPathsByResource">The target dictionary for reference propagation metadata.</param>
    /// <param name="resourceSchemasPath">The JSON label used for diagnostics.</param>
    private static void AddResourceDescriptors(
        JsonObject resourceSchemas,
        string projectName,
        Dictionary<QualifiedResourceName, Dictionary<string, DescriptorPathInfo>> descriptorPathsByResource,
        Dictionary<QualifiedResourceName, List<ReferenceJsonPathInfo>> referenceJsonPathsByResource,
        string resourceSchemasPath
    )
    {
        foreach (var resourceSchemaEntry in OrderResourceSchemas(resourceSchemas, resourceSchemasPath))
        {
            var qualifiedResourceName = new QualifiedResourceName(
                projectName,
                resourceSchemaEntry.ResourceName
            );

            if (descriptorPathsByResource.ContainsKey(qualifiedResourceName))
            {
                throw new InvalidOperationException(
                    $"Descriptor paths for resource '{resourceSchemaEntry.ResourceName}' are already defined."
                );
            }

            descriptorPathsByResource[qualifiedResourceName] = ExtractDescriptorPathsForResource(
                resourceSchemaEntry.ResourceSchema,
                projectName
            );
            referenceJsonPathsByResource[qualifiedResourceName] = ExtractReferenceJsonPaths(
                resourceSchemaEntry.ResourceSchema
            );
        }
    }

    /// <summary>
    /// Extracts descriptor paths for a single resource, preferring explicit <c>documentPathsMapping</c>
    /// entries and falling back to identity-path inference when mappings are not present.
    /// </summary>
    /// <param name="resourceSchema">The resource schema payload.</param>
    /// <param name="projectName">The owning project name.</param>
    /// <returns>A mapping of canonical JSONPath to descriptor path metadata.</returns>
    private static Dictionary<string, DescriptorPathInfo> ExtractDescriptorPathsForResource(
        JsonObject resourceSchema,
        string projectName
    )
    {
        if (resourceSchema["documentPathsMapping"] is not JsonObject documentPathsMapping)
        {
            return ExtractDescriptorPathsFromIdentityJsonPaths(resourceSchema, projectName);
        }

        Dictionary<string, DescriptorPathInfo> descriptorPathsByJsonPath = new(StringComparer.Ordinal);

        foreach (var mapping in OrderDocumentPathsMappingEntries(documentPathsMapping))
        {
            if (mapping.Value is null)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be non-null, invalid ApiSchema."
                );
            }

            if (mapping.Value is not JsonObject mappingObject)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be objects, invalid ApiSchema."
                );
            }

            var isReference =
                mappingObject["isReference"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isReference to be on documentPathsMapping entry, invalid ApiSchema."
                );

            if (!isReference)
            {
                continue;
            }

            var isDescriptor =
                mappingObject["isDescriptor"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isDescriptor to be on documentPathsMapping entry, invalid ApiSchema."
                );

            if (!isDescriptor)
            {
                continue;
            }

            var descriptorPath = RequireString(mappingObject, "path");
            var descriptorProjectName = RequireString(mappingObject, "projectName");
            var descriptorResourceName = RequireString(mappingObject, "resourceName");
            var descriptorJsonPath = JsonPathExpressionCompiler.Compile(descriptorPath);

            if (
                !descriptorPathsByJsonPath.TryAdd(
                    descriptorJsonPath.Canonical,
                    new DescriptorPathInfo(
                        descriptorJsonPath,
                        new QualifiedResourceName(descriptorProjectName, descriptorResourceName)
                    )
                )
            )
            {
                throw new InvalidOperationException(
                    $"Descriptor path '{descriptorJsonPath.Canonical}' is already defined."
                );
            }
        }

        return descriptorPathsByJsonPath;
    }

    /// <summary>
    /// Infers descriptor paths from <c>identityJsonPaths</c> by locating identity properties that
    /// represent descriptor values.
    /// </summary>
    /// <param name="resourceSchema">The resource schema payload.</param>
    /// <param name="projectName">The owning project name.</param>
    /// <returns>A mapping of canonical JSONPath to descriptor path metadata.</returns>
    private static Dictionary<string, DescriptorPathInfo> ExtractDescriptorPathsFromIdentityJsonPaths(
        JsonObject resourceSchema,
        string projectName
    )
    {
        if (resourceSchema["identityJsonPaths"] is not JsonArray identityJsonPaths)
        {
            return new Dictionary<string, DescriptorPathInfo>(StringComparer.Ordinal);
        }

        Dictionary<string, DescriptorPathInfo> descriptorPathsByJsonPath = new(StringComparer.Ordinal);

        foreach (var identityJsonPath in identityJsonPaths)
        {
            if (identityJsonPath is null)
            {
                throw new InvalidOperationException(
                    "Expected identityJsonPaths to not contain null entries, invalid ApiSchema."
                );
            }

            var identityPath = JsonPathExpressionCompiler.Compile(identityJsonPath.GetValue<string>());

            if (!TryGetDescriptorResourceName(identityPath, out var descriptorResourceName))
            {
                continue;
            }

            var descriptorResource = new QualifiedResourceName(projectName, descriptorResourceName);

            if (
                !descriptorPathsByJsonPath.TryAdd(
                    identityPath.Canonical,
                    new DescriptorPathInfo(identityPath, descriptorResource)
                )
            )
            {
                throw new InvalidOperationException(
                    $"Descriptor path '{identityPath.Canonical}' is already defined."
                );
            }
        }

        return descriptorPathsByJsonPath;
    }

    /// <summary>
    /// Determines whether an identity JSONPath corresponds to a descriptor property and returns the
    /// inferred descriptor resource name.
    /// </summary>
    /// <param name="identityPath">The identity JSONPath to inspect.</param>
    /// <param name="descriptorResourceName">The inferred descriptor resource name when matched.</param>
    /// <returns><see langword="true"/> when the identity path targets a descriptor property.</returns>
    private static bool TryGetDescriptorResourceName(
        JsonPathExpression identityPath,
        out string descriptorResourceName
    )
    {
        descriptorResourceName = string.Empty;

        if (identityPath.Segments.Count == 0)
        {
            return false;
        }

        if (identityPath.Segments[^1] is not JsonPathSegment.Property property)
        {
            return false;
        }

        // TODO: Not a fan of string inspection, but needed right now for GeneralStudentProgramAssociation, which has
        // identity path $.programReference.programTypeDescriptor but no documentPathsMapping,
        if (!property.Name.EndsWith("Descriptor", StringComparison.Ordinal))
        {
            return false;
        }

        descriptorResourceName = RelationalNameConventions.ToPascalCase(property.Name);
        return true;
    }

    /// <summary>
    /// Extracts reference JSONPath propagation rules from <c>documentPathsMapping</c>, which are used to
    /// infer descriptor paths by following references between resources.
    /// </summary>
    /// <param name="resourceSchema">The resource schema payload.</param>
    /// <returns>A list of reference propagation metadata entries.</returns>
    private static List<ReferenceJsonPathInfo> ExtractReferenceJsonPaths(JsonObject resourceSchema)
    {
        if (resourceSchema["documentPathsMapping"] is not JsonObject documentPathsMapping)
        {
            return [];
        }

        List<ReferenceJsonPathInfo> referenceJsonPaths = new();

        foreach (var mapping in OrderDocumentPathsMappingEntries(documentPathsMapping))
        {
            if (mapping.Value is null)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be non-null, invalid ApiSchema."
                );
            }

            if (mapping.Value is not JsonObject mappingObject)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be objects, invalid ApiSchema."
                );
            }

            var isReference =
                mappingObject["isReference"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isReference to be on documentPathsMapping entry, invalid ApiSchema."
                );

            if (!isReference)
            {
                continue;
            }

            if (!mappingObject.TryGetPropertyValue("referenceJsonPaths", out var referenceJsonPathsNode))
            {
                continue;
            }

            if (referenceJsonPathsNode is null)
            {
                continue;
            }

            if (referenceJsonPathsNode is not JsonArray referenceJsonPathsArray)
            {
                throw new InvalidOperationException(
                    "Expected referenceJsonPaths to be an array on documentPathsMapping entry, "
                        + "invalid ApiSchema."
                );
            }

            var referencedProjectName = RequireString(mappingObject, "projectName");
            var referencedResourceName = RequireString(mappingObject, "resourceName");
            var referencedResource = new QualifiedResourceName(referencedProjectName, referencedResourceName);

            foreach (var referenceJsonPath in referenceJsonPathsArray)
            {
                if (referenceJsonPath is null)
                {
                    throw new InvalidOperationException(
                        "Expected referenceJsonPaths to not contain null entries, invalid ApiSchema."
                    );
                }

                if (referenceJsonPath is not JsonObject referenceJsonPathObject)
                {
                    throw new InvalidOperationException(
                        "Expected referenceJsonPaths entries to be objects, invalid ApiSchema."
                    );
                }

                var identityJsonPath = RequireString(referenceJsonPathObject, "identityJsonPath");
                var referenceJsonPathValue = RequireString(referenceJsonPathObject, "referenceJsonPath");

                referenceJsonPaths.Add(
                    new ReferenceJsonPathInfo(
                        referencedResource,
                        JsonPathExpressionCompiler.Compile(identityJsonPath),
                        JsonPathExpressionCompiler.Compile(referenceJsonPathValue)
                    )
                );
            }
        }

        return referenceJsonPaths;
    }

    /// <summary>
    /// Orders <c>documentPathsMapping</c> entries deterministically by key.
    /// </summary>
    /// <param name="documentPathsMapping">The mapping object.</param>
    /// <returns>An ordered list of mapping entries.</returns>
    private static IReadOnlyList<KeyValuePair<string, JsonNode?>> OrderDocumentPathsMappingEntries(
        JsonObject documentPathsMapping
    )
    {
        return documentPathsMapping.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
    }
}
