// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Validation;

/// <summary>
/// Validates effective schema set invariants required for deterministic relational model derivation.
/// </summary>
internal static class RelationalModelSetValidation
{
    /// <summary>
    /// Represents an index of effective schema resources used for cross-validation.
    /// </summary>
    /// <param name="Resources">All qualified resource names in the effective schema set.</param>
    /// <param name="IsAbstractByResource">Maps each resource to whether it is abstract.</param>
    internal sealed record EffectiveSchemaResourceIndex(
        IReadOnlySet<QualifiedResourceName> Resources,
        IReadOnlyDictionary<QualifiedResourceName, bool> IsAbstractByResource
    );

    /// <summary>
    /// Builds a resource index from the supplied effective schema set.
    /// </summary>
    /// <param name="effectiveSchemaSet">The normalized effective schema set.</param>
    /// <returns>The resource index for cross-validation.</returns>
    internal static EffectiveSchemaResourceIndex BuildEffectiveSchemaResourceIndex(
        EffectiveSchemaSet effectiveSchemaSet
    )
    {
        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        HashSet<QualifiedResourceName> resources = new();
        Dictionary<QualifiedResourceName, bool> isAbstractByResource = new();

        foreach (var project in effectiveSchemaSet.ProjectsInEndpointOrder)
        {
            if (project is null)
            {
                throw new InvalidOperationException(
                    "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
                );
            }

            if (project.ProjectSchema is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema to be present in EffectiveProjectSchema."
                );
            }

            var projectName = RequireNonEmpty(project.ProjectName, "ProjectName");
            var resourceSchemas = RequireObject(
                project.ProjectSchema["resourceSchemas"],
                "projectSchema.resourceSchemas"
            );

            AddResourceEntries(
                resources,
                isAbstractByResource,
                resourceSchemas,
                projectName,
                "projectSchema.resourceSchemas",
                isAbstract: false
            );

            if (project.ProjectSchema["abstractResources"] is JsonObject abstractResources)
            {
                AddResourceEntries(
                    resources,
                    isAbstractByResource,
                    abstractResources,
                    projectName,
                    "projectSchema.abstractResources",
                    isAbstract: true
                );
            }
        }

        return new EffectiveSchemaResourceIndex(resources, isAbstractByResource);
    }

    /// <summary>
    /// Validates the effective schema metadata and resource key seed against the loaded schema payload.
    /// </summary>
    /// <param name="effectiveSchemaSet">The normalized effective schema set.</param>
    /// <param name="effectiveResources">The resource index derived from the schema payload.</param>
    internal static void ValidateEffectiveSchemaInfo(
        EffectiveSchemaSet effectiveSchemaSet,
        EffectiveSchemaResourceIndex effectiveResources
    )
    {
        if (effectiveSchemaSet.EffectiveSchema is null)
        {
            throw new InvalidOperationException("EffectiveSchemaSet.EffectiveSchema must be provided.");
        }

        ValidateSchemaComponentsInEndpointOrder(effectiveSchemaSet);

        if (effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.ResourceKeysInIdOrder must be provided."
            );
        }

        if (
            effectiveSchemaSet.EffectiveSchema.ResourceKeyCount
            != effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Count
        )
        {
            throw new InvalidOperationException(
                $"EffectiveSchemaInfo.ResourceKeyCount ({effectiveSchemaSet.EffectiveSchema.ResourceKeyCount}) "
                    + "does not match ResourceKeysInIdOrder count "
                    + $"({effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Count})."
            );
        }

        if (effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Any(entry => entry is null))
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.ResourceKeysInIdOrder must not contain null entries."
            );
        }

        var duplicateIds = effectiveSchemaSet
            .EffectiveSchema.ResourceKeysInIdOrder.GroupBy(entry => entry.ResourceKeyId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id)
            .ToArray();

        var duplicateResources = effectiveSchemaSet
            .EffectiveSchema.ResourceKeysInIdOrder.GroupBy(entry => entry.Resource)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        if (duplicateIds.Length > 0 || duplicateResources.Length > 0)
        {
            List<string> messageParts = new();

            if (duplicateIds.Length > 0)
            {
                messageParts.Add(
                    "Duplicate ResourceKeyId values detected: " + string.Join(", ", duplicateIds)
                );
            }

            if (duplicateResources.Length > 0)
            {
                messageParts.Add(
                    "Duplicate resource keys detected for: "
                        + string.Join(", ", duplicateResources.Select(FormatResource))
                );
            }

            throw new InvalidOperationException(string.Join(" ", messageParts));
        }

        var resourceKeysByResource = effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.ToDictionary(
            entry => entry.Resource
        );

        var missingResources = effectiveResources
            .Resources.Where(resource => !resourceKeysByResource.ContainsKey(resource))
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        var extraResources = resourceKeysByResource
            .Where(entry => !effectiveResources.Resources.Contains(entry.Key))
            .Select(entry => entry.Key)
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        if (missingResources.Length > 0 || extraResources.Length > 0)
        {
            List<string> messageParts = new();

            if (missingResources.Length > 0)
            {
                messageParts.Add(
                    "Missing resource keys for: " + string.Join(", ", missingResources.Select(FormatResource))
                );
            }

            if (extraResources.Length > 0)
            {
                messageParts.Add(
                    "Resource keys reference unknown resources: "
                        + string.Join(", ", extraResources.Select(FormatResource))
                );
            }

            throw new InvalidOperationException(string.Join(" ", messageParts));
        }

        ValidateResourceKeyAbstractness(
            effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder,
            effectiveResources.IsAbstractByResource
        );
    }

    /// <summary>
    /// Validates that <see cref="EffectiveSchemaInfo.SchemaComponentsInEndpointOrder"/> is complete, unique,
    /// and sorted, and that it matches <see cref="EffectiveSchemaSet.ProjectsInEndpointOrder"/>.
    /// </summary>
    private static void ValidateSchemaComponentsInEndpointOrder(EffectiveSchemaSet effectiveSchemaSet)
    {
        if (effectiveSchemaSet.EffectiveSchema.SchemaComponentsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.SchemaComponentsInEndpointOrder must be provided."
            );
        }

        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        if (
            effectiveSchemaSet.EffectiveSchema.SchemaComponentsInEndpointOrder.Any(component =>
                component is null
            )
        )
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.SchemaComponentsInEndpointOrder must not contain null entries."
            );
        }

        var schemaComponentEndpointNames = effectiveSchemaSet
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Select(component =>
                RequireNonEmpty(
                    component.ProjectEndpointName,
                    "SchemaComponentsInEndpointOrder.ProjectEndpointName"
                )
            )
            .ToArray();

        var duplicateSchemaComponents = schemaComponentEndpointNames
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (duplicateSchemaComponents.Length > 0)
        {
            throw new InvalidOperationException(
                "SchemaComponentsInEndpointOrder contains duplicate ProjectEndpointName values: "
                    + string.Join(", ", duplicateSchemaComponents)
            );
        }

        for (var index = 1; index < schemaComponentEndpointNames.Length; index++)
        {
            if (
                StringComparer.Ordinal.Compare(
                    schemaComponentEndpointNames[index - 1],
                    schemaComponentEndpointNames[index]
                ) > 0
            )
            {
                throw new InvalidOperationException(
                    "SchemaComponentsInEndpointOrder is not sorted by ProjectEndpointName: "
                        + $"'{schemaComponentEndpointNames[index - 1]}' appears before "
                        + $"'{schemaComponentEndpointNames[index]}'."
                );
            }
        }

        var projectEndpointNames = effectiveSchemaSet
            .ProjectsInEndpointOrder.Select(project =>
                RequireNonEmpty(project.ProjectEndpointName, "ProjectEndpointName")
            )
            .ToArray();

        var schemaComponentSet = new HashSet<string>(schemaComponentEndpointNames, StringComparer.Ordinal);
        var projectSet = new HashSet<string>(projectEndpointNames, StringComparer.Ordinal);

        var missing = projectSet
            .Except(schemaComponentSet)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var extra = schemaComponentSet
            .Except(projectSet)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0 && extra.Length == 0)
        {
            return;
        }

        List<string> messageParts = new();

        if (missing.Length > 0)
        {
            messageParts.Add(
                "SchemaComponentsInEndpointOrder is missing projects: " + string.Join(", ", missing)
            );
        }

        if (extra.Length > 0)
        {
            messageParts.Add(
                "SchemaComponentsInEndpointOrder contains unknown projects: " + string.Join(", ", extra)
            );
        }

        throw new InvalidOperationException(string.Join(" ", messageParts));
    }

    /// <summary>
    /// Validates that <c>documentPathsMapping</c> reference targets resolve to known resources in the effective schema set.
    /// </summary>
    /// <param name="effectiveSchemaSet">The normalized effective schema set.</param>
    /// <param name="effectiveResources">All resources in the effective schema set.</param>
    internal static void ValidateDocumentPathsMappingTargets(
        EffectiveSchemaSet effectiveSchemaSet,
        IReadOnlySet<QualifiedResourceName> effectiveResources
    )
    {
        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        foreach (var project in effectiveSchemaSet.ProjectsInEndpointOrder)
        {
            if (project is null)
            {
                throw new InvalidOperationException(
                    "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
                );
            }

            if (project.ProjectSchema is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema to be present in EffectiveProjectSchema."
                );
            }

            var projectName = RequireNonEmpty(project.ProjectName, "ProjectName");
            var resourceSchemas = RequireObject(
                project.ProjectSchema["resourceSchemas"],
                "projectSchema.resourceSchemas"
            );

            ValidateDocumentPathsMappingTargetsForResourceSchemas(
                projectName,
                resourceSchemas,
                effectiveResources,
                "projectSchema.resourceSchemas"
            );

            if (project.ProjectSchema["abstractResources"] is JsonObject abstractResources)
            {
                ValidateDocumentPathsMappingTargetsForResourceSchemas(
                    projectName,
                    abstractResources,
                    effectiveResources,
                    "projectSchema.abstractResources"
                );
            }
        }
    }

    /// <summary>
    /// Validates that each <c>referenceJsonPaths[*].identityJsonPath</c> resolves to a target resource
    /// identity JSONPath.
    /// </summary>
    /// <param name="effectiveSchemaSet">The normalized effective schema set.</param>
    /// <param name="effectiveResources">All resources in the effective schema set.</param>
    internal static void ValidateReferenceIdentityJsonPaths(
        EffectiveSchemaSet effectiveSchemaSet,
        IReadOnlySet<QualifiedResourceName> effectiveResources
    )
    {
        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        var resourceSchemaIndex = BuildResourceSchemaIndex(effectiveSchemaSet);
        Dictionary<QualifiedResourceName, HashSet<string>> identityPathsByResource = new();

        foreach (var project in effectiveSchemaSet.ProjectsInEndpointOrder)
        {
            if (project is null)
            {
                throw new InvalidOperationException(
                    "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
                );
            }

            if (project.ProjectSchema is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema to be present in EffectiveProjectSchema."
                );
            }

            var projectName = RequireNonEmpty(project.ProjectName, "ProjectName");
            var resourceSchemas = RequireObject(
                project.ProjectSchema["resourceSchemas"],
                "projectSchema.resourceSchemas"
            );

            ValidateReferenceIdentityJsonPathsForResourceSchemas(
                projectName,
                resourceSchemas,
                effectiveResources,
                resourceSchemaIndex,
                identityPathsByResource,
                "projectSchema.resourceSchemas"
            );

            if (project.ProjectSchema["abstractResources"] is JsonObject abstractResources)
            {
                ValidateReferenceIdentityJsonPathsForResourceSchemas(
                    projectName,
                    abstractResources,
                    effectiveResources,
                    resourceSchemaIndex,
                    identityPathsByResource,
                    "projectSchema.abstractResources"
                );
            }
        }
    }

    /// <summary>
    /// Validates that each <c>documentPathsMapping.referenceJsonPaths[*].identityJsonPath</c> entry references an
    /// identity path that exists on the effective target resource schemas for all resource schema entries in the
    /// supplied object.
    /// </summary>
    private static void ValidateReferenceIdentityJsonPathsForResourceSchemas(
        string projectName,
        JsonObject resourceSchemas,
        IReadOnlySet<QualifiedResourceName> effectiveResources,
        IReadOnlyDictionary<QualifiedResourceName, JsonObject> resourceSchemaIndex,
        IDictionary<QualifiedResourceName, HashSet<string>> identityPathsByResource,
        string resourceSchemasPath
    )
    {
        foreach (
            var resourceSchemaEntry in OrderResourceSchemas(
                resourceSchemas,
                resourceSchemasPath,
                requireNonEmptyKey: true
            )
        )
        {
            ValidateReferenceIdentityJsonPathsForResource(
                projectName,
                resourceSchemaEntry.ResourceName,
                resourceSchemaEntry.ResourceSchema,
                effectiveResources,
                resourceSchemaIndex,
                identityPathsByResource
            );
        }
    }

    /// <summary>
    /// Validates that each reference mapping's identity path exists on the referenced resource when the
    /// referenced resource is present in the effective schema set.
    /// </summary>
    private static void ValidateReferenceIdentityJsonPathsForResource(
        string projectName,
        string resourceName,
        JsonObject resourceSchema,
        IReadOnlySet<QualifiedResourceName> effectiveResources,
        IReadOnlyDictionary<QualifiedResourceName, JsonObject> resourceSchemaIndex,
        IDictionary<QualifiedResourceName, HashSet<string>> identityPathsByResource
    )
    {
        if (resourceSchema["documentPathsMapping"] is not JsonObject documentPathsMapping)
        {
            return;
        }

        foreach (var mapping in documentPathsMapping.OrderBy(entry => entry.Key, StringComparer.Ordinal))
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

            if (isDescriptor)
            {
                continue;
            }

            if (!mappingObject.TryGetPropertyValue("referenceJsonPaths", out var referenceJsonPathsNode))
            {
                throw new InvalidOperationException(
                    "Expected referenceJsonPaths to be present on documentPathsMapping entry, "
                        + "invalid ApiSchema."
                );
            }

            if (referenceJsonPathsNode is null)
            {
                throw new InvalidOperationException(
                    "Expected referenceJsonPaths to be present on documentPathsMapping entry, "
                        + "invalid ApiSchema."
                );
            }

            if (referenceJsonPathsNode is not JsonArray referenceJsonPathsArray)
            {
                throw new InvalidOperationException(
                    "Expected referenceJsonPaths to be an array on documentPathsMapping entry, "
                        + "invalid ApiSchema."
                );
            }

            var targetProjectName = RequireString(mappingObject, "projectName");
            var targetResourceName = RequireString(mappingObject, "resourceName");
            var targetResource = new QualifiedResourceName(targetProjectName, targetResourceName);

            if (!effectiveResources.Contains(targetResource))
            {
                continue;
            }

            var targetIdentityPaths = GetIdentityPathsForResource(
                targetResource,
                resourceSchemaIndex,
                identityPathsByResource
            );

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
                var identityPath = JsonPathExpressionCompiler.Compile(identityJsonPath);

                if (targetIdentityPaths.Contains(identityPath.Canonical))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"documentPathsMapping entry '{mapping.Key}' on resource '{projectName}:{resourceName}' "
                        + $"references identityJsonPath '{identityPath.Canonical}' which does not exist "
                        + $"in target resource '{FormatResource(targetResource)}'."
                );
            }
        }
    }

    /// <summary>
    /// Builds an index of all resource schemas keyed by qualified resource name.
    /// </summary>
    private static IReadOnlyDictionary<QualifiedResourceName, JsonObject> BuildResourceSchemaIndex(
        EffectiveSchemaSet effectiveSchemaSet
    )
    {
        Dictionary<QualifiedResourceName, JsonObject> index = new();

        foreach (var project in effectiveSchemaSet.ProjectsInEndpointOrder ?? [])
        {
            if (project is null)
            {
                throw new InvalidOperationException(
                    "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
                );
            }

            if (project.ProjectSchema is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema to be present in EffectiveProjectSchema."
                );
            }

            var projectName = RequireNonEmpty(project.ProjectName, "ProjectName");
            var resourceSchemas = RequireObject(
                project.ProjectSchema["resourceSchemas"],
                "projectSchema.resourceSchemas"
            );

            AddResourceSchemaEntries(index, resourceSchemas, projectName, "projectSchema.resourceSchemas");

            if (project.ProjectSchema["abstractResources"] is JsonObject abstractResources)
            {
                AddResourceSchemaEntries(
                    index,
                    abstractResources,
                    projectName,
                    "projectSchema.abstractResources"
                );
            }
        }

        return index;
    }

    /// <summary>
    /// Adds resource schema entries to a schema index, throwing when required structure is missing.
    /// </summary>
    private static void AddResourceSchemaEntries(
        IDictionary<QualifiedResourceName, JsonObject> index,
        JsonObject resourceSchemas,
        string projectName,
        string resourceSchemasPath
    )
    {
        foreach (
            var entry in OrderResourceSchemas(resourceSchemas, resourceSchemasPath, requireNonEmptyKey: true)
        )
        {
            var resourceKey = new QualifiedResourceName(projectName, entry.ResourceName);
            index[resourceKey] = entry.ResourceSchema;
        }
    }

    /// <summary>
    /// Returns the compiled identity path set for a resource, caching results for reuse across validations.
    /// </summary>
    private static HashSet<string> GetIdentityPathsForResource(
        QualifiedResourceName resource,
        IReadOnlyDictionary<QualifiedResourceName, JsonObject> resourceSchemaIndex,
        IDictionary<QualifiedResourceName, HashSet<string>> identityPathsByResource
    )
    {
        if (identityPathsByResource.TryGetValue(resource, out var existing))
        {
            return existing;
        }

        if (!resourceSchemaIndex.TryGetValue(resource, out var resourceSchema))
        {
            throw new InvalidOperationException(
                $"Resource schema not found for resource '{FormatResource(resource)}'."
            );
        }

        if (resourceSchema["identityJsonPaths"] is not JsonArray identityJsonPaths)
        {
            throw new InvalidOperationException(
                $"Expected identityJsonPaths to be present on resource '{FormatResource(resource)}'."
            );
        }

        HashSet<string> identityPaths = new(StringComparer.Ordinal);

        foreach (var identityJsonPath in identityJsonPaths)
        {
            if (identityJsonPath is null)
            {
                throw new InvalidOperationException(
                    "Expected identityJsonPaths to not contain null entries, invalid ApiSchema."
                );
            }

            var identityPath = JsonPathExpressionCompiler.Compile(identityJsonPath.GetValue<string>());
            identityPaths.Add(identityPath.Canonical);
        }

        identityPathsByResource[resource] = identityPaths;

        return identityPaths;
    }

    /// <summary>
    /// Validates <c>documentPathsMapping</c> reference targets for all resources contained in a schema object.
    /// </summary>
    /// <param name="projectName">The owning project name.</param>
    /// <param name="resourceSchemas">The <c>resourceSchemas</c> or <c>abstractResources</c> object.</param>
    /// <param name="effectiveResources">All resources in the effective schema set.</param>
    /// <param name="resourceSchemasPath">The JSON label used for diagnostics.</param>
    private static void ValidateDocumentPathsMappingTargetsForResourceSchemas(
        string projectName,
        JsonObject resourceSchemas,
        IReadOnlySet<QualifiedResourceName> effectiveResources,
        string resourceSchemasPath
    )
    {
        foreach (
            var resourceSchemaEntry in OrderResourceSchemas(
                resourceSchemas,
                resourceSchemasPath,
                requireNonEmptyKey: true
            )
        )
        {
            ValidateDocumentPathsMappingTargetsForResource(
                projectName,
                resourceSchemaEntry.ResourceName,
                resourceSchemaEntry.ResourceSchema,
                effectiveResources
            );
        }
    }

    /// <summary>
    /// Validates that each reference entry in <c>documentPathsMapping</c> points to a resource that exists
    /// in the effective schema set.
    /// </summary>
    /// <param name="projectName">The owning project name.</param>
    /// <param name="resourceName">The owning resource name.</param>
    /// <param name="resourceSchema">The resource schema payload.</param>
    /// <param name="effectiveResources">All resources in the effective schema set.</param>
    private static void ValidateDocumentPathsMappingTargetsForResource(
        string projectName,
        string resourceName,
        JsonObject resourceSchema,
        IReadOnlySet<QualifiedResourceName> effectiveResources
    )
    {
        if (resourceSchema["documentPathsMapping"] is not JsonObject documentPathsMapping)
        {
            return;
        }

        foreach (var mapping in documentPathsMapping.OrderBy(entry => entry.Key, StringComparer.Ordinal))
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

            var targetProjectName = RequireString(mappingObject, "projectName");
            var targetResourceName = RequireString(mappingObject, "resourceName");
            var targetResource = new QualifiedResourceName(targetProjectName, targetResourceName);

            if (effectiveResources.Contains(targetResource))
            {
                continue;
            }

            var mappingLabel = FormatDocumentPathsMappingLabel(mapping.Key, mappingObject);

            throw new InvalidOperationException(
                $"documentPathsMapping {mappingLabel} on resource '{projectName}:{resourceName}' "
                    + $"references unknown resource '{targetProjectName}:{targetResourceName}'."
            );
        }
    }

    /// <summary>
    /// Formats a <c>documentPathsMapping</c> entry label for use in error messages.
    /// </summary>
    /// <param name="mappingKey">The mapping entry key.</param>
    /// <param name="mappingObject">The mapping object.</param>
    /// <returns>A formatted entry label.</returns>
    private static string FormatDocumentPathsMappingLabel(string mappingKey, JsonObject mappingObject)
    {
        if (string.IsNullOrWhiteSpace(mappingKey))
        {
            return "entry '<empty>'";
        }

        var path = TryGetOptionalString(mappingObject, "path");

        if (path is null)
        {
            return $"entry '{mappingKey}'";
        }

        return $"entry '{mappingKey}' (path '{path}')";
    }

    /// <summary>
    /// Adds resource entries from a schema object to the effective resource index, validating that a resource
    /// is not defined as both abstract and concrete.
    /// </summary>
    /// <param name="resources">The set of resources to populate.</param>
    /// <param name="isAbstractByResource">The abstractness map to populate.</param>
    /// <param name="resourceSchemas">The <c>resourceSchemas</c> or <c>abstractResources</c> object.</param>
    /// <param name="projectName">The owning project name.</param>
    /// <param name="resourceSchemasPath">The JSON label used for diagnostics.</param>
    /// <param name="isAbstract">Whether the entries are abstract.</param>
    private static void AddResourceEntries(
        HashSet<QualifiedResourceName> resources,
        Dictionary<QualifiedResourceName, bool> isAbstractByResource,
        JsonObject resourceSchemas,
        string projectName,
        string resourceSchemasPath,
        bool isAbstract
    )
    {
        foreach (var resourceSchemaEntry in resourceSchemas)
        {
            if (resourceSchemaEntry.Value is null)
            {
                throw new InvalidOperationException(
                    $"Expected {resourceSchemasPath} entries to be non-null, invalid ApiSchema."
                );
            }

            if (resourceSchemaEntry.Value is not JsonObject resourceSchema)
            {
                throw new InvalidOperationException(
                    $"Expected {resourceSchemasPath} entries to be objects, invalid ApiSchema."
                );
            }

            if (string.IsNullOrWhiteSpace(resourceSchemaEntry.Key))
            {
                throw new InvalidOperationException(
                    "Expected resource schema entry key to be non-empty, invalid ApiSchema."
                );
            }

            var resourceName = GetResourceName(resourceSchemaEntry.Key, resourceSchema);
            var resource = new QualifiedResourceName(projectName, resourceName);

            if (resources.Add(resource))
            {
                isAbstractByResource[resource] = isAbstract;
                continue;
            }

            if (!isAbstractByResource.TryGetValue(resource, out var existingIsAbstract))
            {
                continue;
            }

            if (existingIsAbstract == isAbstract)
            {
                continue;
            }

            var existingLocation = existingIsAbstract
                ? "projectSchema.abstractResources"
                : "projectSchema.resourceSchemas";
            var duplicateLocation = isAbstract
                ? "projectSchema.abstractResources"
                : "projectSchema.resourceSchemas";

            throw new InvalidOperationException(
                $"Resource '{FormatResource(resource)}' is defined in both {existingLocation} "
                    + $"and {duplicateLocation}."
            );
        }
    }

    /// <summary>
    /// Validates that each resource key entry's abstractness matches the effective schema payload.
    /// </summary>
    /// <param name="resourceKeys">The resource key entries to validate.</param>
    /// <param name="isAbstractByResource">The expected abstractness map keyed by resource.</param>
    private static void ValidateResourceKeyAbstractness(
        IReadOnlyList<ResourceKeyEntry> resourceKeys,
        IReadOnlyDictionary<QualifiedResourceName, bool> isAbstractByResource
    )
    {
        foreach (var entry in resourceKeys)
        {
            if (!isAbstractByResource.TryGetValue(entry.Resource, out var expectedIsAbstract))
            {
                throw new InvalidOperationException(
                    $"Resource key entry for resource '{FormatResource(entry.Resource)}' "
                        + "does not correspond to a resource in the effective schema set."
                );
            }

            if (entry.IsAbstractResource == expectedIsAbstract)
            {
                continue;
            }

            var expectedLabel = expectedIsAbstract ? "abstract" : "concrete";
            var actualLabel = entry.IsAbstractResource ? "abstract" : "concrete";

            throw new InvalidOperationException(
                $"Resource key entry for resource '{FormatResource(entry.Resource)}' has "
                    + $"IsAbstractResource={entry.IsAbstractResource} ({actualLabel}) but expected "
                    + $"IsAbstractResource={expectedIsAbstract} ({expectedLabel})."
            );
        }
    }
}
